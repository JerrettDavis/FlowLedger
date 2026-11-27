using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.SimpleFin;

/// <summary>
/// SimpleFIN Bridge provider. One access URL = one "member" covering every institution linked at the Bridge.
/// A sync costs exactly one <c>GET /accounts</c> (SimpleFIN allows ~24/day): the response is cached for the
/// scoped lifetime and serves both accounts and every account's transactions.
/// </summary>
public sealed partial class SimpleFinFinancialDataProvider : IFinancialDataProvider
{
    public const string MemberId = "simplefin";

    private readonly HttpClient _http;
    private readonly SimpleFinOptions _options;
    private readonly ILogger<SimpleFinFinancialDataProvider> _logger;
    private readonly TimeProvider _time;
    private Task<SfResponse>? _cached;
    private Task<bool>? _backfill;
    private DateTimeOffset _fetchedAt;
    private int _fetchedDays;

    // The cursor carries "<date>|bf<days>" once a deep backfill of that many days completed, so it never runs again (#128).
    private const string BackfillMarker = "|bf";

    public SimpleFinFinancialDataProvider(
        HttpClient http, IOptions<SimpleFinOptions> options, ILogger<SimpleFinFinancialDataProvider> logger, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderName => "SimpleFIN";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Accounts | ProviderCapabilities.IncrementalTransactions;

    /// <summary>Operator-held access URL (household tenant only), connection is a local record, balances use the holdings sign.</summary>
    public const ConnectorTraits DeclaredTraits = ConnectorTraits.ProvisionsLocally | ConnectorTraits.ReportsHoldingsSign;

    public ConnectorTraits Traits => DeclaredTraits;

    public Task<ProviderMemberRef> BeginConnectionAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return Task.FromResult(new ProviderMemberRef(MemberId, "SimpleFIN Bridge", ConnectionStatus.Connected));
    }

    public Task<ConnectionStatus> GetConnectionStatusAsync(string memberProviderId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return Task.FromResult(ConnectionStatus.Healthy);
    }

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string memberProviderId, CancellationToken cancellationToken = default)
    {
        var res = await FetchAsync(cancellationToken).ConfigureAwait(false);
        return UsableAccounts(res).Select(SimpleFinMapper.ToAccount).ToList();
    }

    public async Task<TransactionPage> GetTransactionsAsync(
        string providerAccountId, SyncCursor cursor, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var res = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var acct = UsableAccounts(res).FirstOrDefault(a => SimpleFinMapper.AccountId(a) == providerAccountId);
        if (acct?.Transactions is null)
        {
            return TransactionPage.Empty(cursor);
        }

        var (cursorDate, backfilledDays) = ParseCursor(cursor);
        var backfillRequested = _options.BackfillDays > _options.HistoryDays;
        var needsBackfill = backfillRequested && backfilledDays < _options.BackfillDays;
        var backfillComplete = backfilledDays >= _options.BackfillDays;
        if (needsBackfill)
        {
            backfillComplete = await EnsureBackfilledAsync(res, cancellationToken).ConfigureAwait(false);
        }

        var floor = cursor.IsInitial || needsBackfill || cursorDate is null
            ? DateOnly.MinValue
            : cursorDate.Value.AddDays(-Math.Max(0, _options.OverlapDays));

        var dated = acct.Transactions.Where(SimpleFinMapper.HasUsableDate).ToList();
        var skipped = acct.Transactions.Count - dated.Count;
        var items = dated
            .Select(t => SimpleFinMapper.ToTransaction(acct, t, _time))
            .Where(t => t.PostedDate >= floor)
            .OrderBy(t => t.PostedDate)
            .ToList();

        var settled = items.Where(t => !t.IsPending).Select(t => t.PostedDate).DefaultIfEmpty().Max();
        DateOnly? nextDate = cursorDate;
        if (settled != default)
        {
            nextDate = settled;
        }
        else if (cursorDate is null && items.Count > 0)
        {
            // Only pending rows so far: everything older than the earliest one has been seen, so do not re-read the whole window
            // on every sync. A truly empty account keeps its initial cursor so late-arriving history is not skipped.
            nextDate = items.Min(t => t.PostedDate).AddDays(-1);
        }

        var next = new SyncCursor(FormatCursor(nextDate, backfillRequested && backfillComplete ? _options.BackfillDays : 0));
        return new TransactionPage(items, next.Value.Length == 0 ? cursor : next, HasMore: false) { SkippedCount = skipped };
    }

    /// <summary>Accounts whose currency the bridge did not report as ISO are left out (and logged), never booked as USD (#124).</summary>
    private IEnumerable<SfAccount> UsableAccounts(SfResponse res)
    {
        foreach (var a in res.Accounts!)
        {
            if (SimpleFinMapper.HasUsableCurrency(a))
            {
                yield return a;
            }
            else
            {
                LogSkippingAccountNoCurrency();
            }
        }
    }

    private static (DateOnly? Date, int BackfilledDays) ParseCursor(SyncCursor cursor)
    {
        if (cursor.IsInitial)
        {
            return (null, 0);
        }

        var parts = cursor.Value.Split(BackfillMarker, 2);
        var days = parts.Length == 2 && int.TryParse(parts[1], out var d) ? d : 0;
        return (DateOnly.TryParse(parts[0], out var date) ? date : null, days);
    }

    private static string FormatCursor(DateOnly? date, int backfilledDays)
    {
        var text = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        return backfilledDays > 0 ? text + BackfillMarker + backfilledDays : text;
    }

    /// <summary>Runs the deep backfill at most once per provider instance; true when it completed (nothing older to fetch).</summary>
    private Task<bool> EnsureBackfilledAsync(SfResponse body, CancellationToken ct)
    {
        var task = _backfill ??= BackfillAsync(body, _fetchedAt, _fetchedDays, Window, CancellationToken.None);
        task.ContinueWith(t => Interlocked.CompareExchange(ref _backfill, null, t), CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    public Task VerifyWebhookAsync(ReadOnlyMemory<byte> rawBody, string signatureHeader, CancellationToken cancellationToken = default) =>
        throw new InvalidWebhookSignatureException("SimpleFIN does not send webhooks.");

    public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SimpleFIN does not send webhooks.");

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.AccessUrl))
        {
            throw new NeedsUserActionProviderException(
                "SimpleFin:AccessUrl is not set.",
                "Claim a SimpleFIN setup token with scripts/simplefin-claim.ps1.");
        }
    }

    private Task<SfResponse> FetchAsync(CancellationToken ct)
    {
        EnsureConfigured();
        // The shared fetch must not run under the first caller's token: cancelling that caller would otherwise leave a cancelled task
        // cached for every later caller. Each caller waits with its own token instead, and a failed fetch is dropped from the cache (#128).
        var task = _cached ??= FetchCoreAsync(CancellationToken.None);
        task.ContinueWith(t => Interlocked.CompareExchange(ref _cached, null, t), CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    // SimpleFIN rejects ranges "exceeding" 90 days; stay one day inside so clock skew can't trip the cap.
    private const int Window = 89;

    private async Task<SfResponse> FetchCoreAsync(CancellationToken ct)
    {
        const int window = Window;
        var days = Math.Clamp(_options.HistoryDays, 1, window);
        var now = _time.GetUtcNow();
        var start = now.AddDays(-days).ToUnixTimeSeconds();
        using var resp = await _http.GetAsync($"accounts?start-date={start}&pending=1", ct).ConfigureAwait(false);

        switch (resp.StatusCode)
        {
            case HttpStatusCode.PaymentRequired:
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.Unauthorized:
                _cached = null;
                throw new NeedsUserActionProviderException(
                    $"SimpleFIN rejected the access URL (HTTP {(int)resp.StatusCode}).",
                    "Check the SimpleFIN subscription or re-claim a setup token.");
            case HttpStatusCode.TooManyRequests:
                _cached = null;
                throw new RateLimitedProviderException("SimpleFIN daily request limit reached.");
        }

        if (!resp.IsSuccessStatusCode)
        {
            _cached = null;
            throw new TransientProviderException($"SimpleFIN returned HTTP {(int)resp.StatusCode}.");
        }

        var body = await resp.Content.ReadFromJsonAsync<SfResponse>(ct).ConfigureAwait(false)
                   ?? throw new FatalProviderException("SimpleFIN returned an empty body.");
        body.Accounts ??= [];

        // Informational notices (e.g. the range being capped) are not failures.
        body.Errors = body.Errors?.Where(e => !e.Contains("capped", StringComparison.OrdinalIgnoreCase)).ToList();
        if (body.Errors is { Count: > 0 })
        {
            // Messages name institutions (e.g. "needs attention"); log them but never the access URL.
            LogProviderIssues(body.Errors.Count, string.Join(" | ", body.Errors));
            if (body.Accounts.Count == 0)
            {
                _cached = null;
                throw new NeedsUserActionProviderException(
                    "SimpleFIN returned no accounts: " + string.Join("; ", body.Errors),
                    "Re-authenticate the institution at the SimpleFIN Bridge.");
            }
        }

        _fetchedAt = now;
        _fetchedDays = days;
        return body;
    }

    /// <summary>Walks backwards from the end of the first window, merging older transactions into <paramref name="body"/>.</summary>
    private async Task<bool> BackfillAsync(SfResponse body, DateTimeOffset now, int coveredDays, int window, CancellationToken ct)
    {
        var total = Math.Min(_options.BackfillDays, 1100);
        var byId = body.Accounts!.ToDictionary(SimpleFinMapper.AccountId);
        var covered = coveredDays;
        var requests = 0;
        while (covered < total && requests < 14)
        {
            var span = Math.Min(window, total - covered);
            var end = now.AddDays(-covered).ToUnixTimeSeconds();
            var start = now.AddDays(-(covered + span)).ToUnixTimeSeconds();
            using var resp = await _http.GetAsync($"accounts?start-date={start}&end-date={end}&pending=1", ct).ConfigureAwait(false);
            requests++;
            if (!resp.IsSuccessStatusCode)
            {
                LogBackfillStopped(covered, (int)resp.StatusCode);
                return false;
            }

            var page = await resp.Content.ReadFromJsonAsync<SfResponse>(ct).ConfigureAwait(false);
            var added = 0;
            foreach (var acct in page?.Accounts ?? [])
            {
                if (acct.Transactions is not { Count: > 0 } || !byId.TryGetValue(SimpleFinMapper.AccountId(acct), out var target))
                {
                    continue;
                }

                (target.Transactions ??= []).AddRange(acct.Transactions);
                added += acct.Transactions.Count;
            }

            LogBackfillWindow(covered, covered + span, added);
            if (added == 0)
            {
                return true; // nothing older is available
            }

            covered += span;
        }

        return covered >= total;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping a SimpleFIN account: no usable ISO currency reported.")]
    private partial void LogSkippingAccountNoCurrency();

    [LoggerMessage(Level = LogLevel.Warning, Message = "SimpleFIN reported {Count} issue(s): {Errors}")]
    private partial void LogProviderIssues(int count, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SimpleFIN backfill stopped at {Covered} days: HTTP {Status}.")]
    private partial void LogBackfillStopped(int covered, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "SimpleFIN backfill window {Covered}-{Next} days: {Count} transaction(s).")]
    private partial void LogBackfillWindow(int covered, int next, int count);
}
