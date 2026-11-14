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
public sealed class SimpleFinFinancialDataProvider : IFinancialDataProvider
{
    public const string MemberId = "simplefin";

    private readonly HttpClient _http;
    private readonly SimpleFinOptions _options;
    private readonly ILogger<SimpleFinFinancialDataProvider> _logger;
    private Task<SfResponse>? _cached;

    public SimpleFinFinancialDataProvider(
        HttpClient http, IOptions<SimpleFinOptions> options, ILogger<SimpleFinFinancialDataProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderName => "SimpleFIN";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Accounts | ProviderCapabilities.IncrementalTransactions;

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
        return res.Accounts!.Select(SimpleFinMapper.ToAccount).ToList();
    }

    public async Task<TransactionPage> GetTransactionsAsync(
        string providerAccountId, SyncCursor cursor, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var res = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var acct = res.Accounts!.FirstOrDefault(a => SimpleFinMapper.AccountId(a) == providerAccountId);
        if (acct?.Transactions is null)
        {
            return TransactionPage.Empty(cursor);
        }

        var floor = cursor.IsInitial || !DateOnly.TryParse(cursor.Value, out var c)
            ? DateOnly.MinValue
            : c.AddDays(-Math.Max(0, _options.OverlapDays));

        var items = acct.Transactions
            .Select(t => SimpleFinMapper.ToTransaction(acct, t))
            .Where(t => t.PostedDate >= floor)
            .OrderBy(t => t.PostedDate)
            .ToList();

        var settled = items.Where(t => !t.IsPending).Select(t => t.PostedDate).DefaultIfEmpty().Max();
        var next = settled == default ? cursor : new SyncCursor(settled.ToString("yyyy-MM-dd"));
        return new TransactionPage(items, next, HasMore: false);
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
        return _cached ??= FetchCoreAsync(ct);
    }

    private async Task<SfResponse> FetchCoreAsync(CancellationToken ct)
    {
        var days = Math.Clamp(_options.HistoryDays, 1, 90);
        var start = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
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

        if (body.Errors is { Count: > 0 })
        {
            // Messages name institutions (e.g. "needs attention"); log them but never the access URL.
            _logger.LogWarning("SimpleFIN reported {Count} issue(s): {Errors}", body.Errors.Count, string.Join(" | ", body.Errors));
            if (body.Accounts.Count == 0)
            {
                _cached = null;
                throw new NeedsUserActionProviderException(
                    "SimpleFIN returned no accounts: " + string.Join("; ", body.Errors),
                    "Re-authenticate the institution at the SimpleFIN Bridge.");
            }
        }

        return body;
    }
}
