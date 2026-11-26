using System.Text.Json;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Quiltt;

/// <summary>
/// Quiltt provider. One FlowLedger tenant = one Quiltt profile (the member id), so accounts of different users are isolated by
/// Quiltt itself and the connector is safe for every tenant (not just the household). The user links institutions in the browser
/// through the Quiltt Connector using a session minted by <see cref="CreateSessionAsync"/>; syncing then reads the profile's
/// accounts and transactions over GraphQL. A scoped instance caches one session, one accounts read and one transactions read,
/// so a whole sync costs a handful of requests regardless of account count.
/// </summary>
public sealed class QuilttFinancialDataProvider : IFinancialDataProvider
{
    public const string Name = "Quiltt";

    private readonly QuilttApiClient _client;
    private readonly QuilttOptions _options;
    private readonly QuilttWebhookVerifier? _verifier;
    private readonly ILogger<QuilttFinancialDataProvider> _logger;

    private QuilttSession? _session;
    private Task<IReadOnlyList<QAccount>>? _accounts;
    private Task<IReadOnlyList<QConnectionNode>>? _connections;
    private (DateOnly From, Task<QTransactionsRead> Task)? _transactions;
    private readonly Dictionary<string, string> _currencyByAccount = new(StringComparer.Ordinal);

    internal QuilttFinancialDataProvider(
        QuilttApiClient client,
        IOptions<QuilttOptions> options,
        QuilttWebhookVerifier? verifier,
        ILogger<QuilttFinancialDataProvider> logger)
    {
        _client = client;
        _options = options.Value;
        _verifier = verifier;
        _logger = logger;
    }

    public string ProviderName => Name;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Accounts
        | ProviderCapabilities.IncrementalTransactions
        | ProviderCapabilities.FullTransactionHistory
        | ProviderCapabilities.WebhookVerification
        | ProviderCapabilities.WebhookParsing
        | ProviderCapabilities.ConnectionHealth;

    /// <summary>The public Connector id the browser opens (not a secret).</summary>
    public string? ConnectorId => _options.ConnectorId;

    /// <summary>URL of the browser script that hosts the Connector.</summary>
    public string ConnectorScriptUrl => _options.ConnectorScriptUrl;

    /// <summary>
    /// Mints a browser-safe session token. With <paramref name="profileId"/> null a new profile is created; otherwise a session for
    /// that existing profile. The token is short-lived and scoped to the profile; the server API key never leaves the server.
    /// </summary>
    public Task<QuilttSession> CreateSessionAsync(string? profileId, CancellationToken cancellationToken = default) =>
        _client.CreateSessionAsync(profileId, cancellationToken);

    // ── Connection lifecycle ─────────────────────────────────────────────────

    /// <summary>Creates the tenant's Quiltt profile (free; no bank is linked until the user finishes the Connector).</summary>
    public async Task<ProviderMemberRef> BeginConnectionAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var session = await _client.CreateSessionAsync(null, cancellationToken).ConfigureAwait(false);
        _session = session;
        return new ProviderMemberRef(session.ProfileId, "Quiltt", ConnectionStatus.ConnectionPending);
    }

    public async Task<ConnectionStatus> GetConnectionStatusAsync(string memberProviderId, CancellationToken cancellationToken = default) =>
        (await GetConnectionHealthAsync(memberProviderId, cancellationToken).ConfigureAwait(false)).Status;

    /// <summary>
    /// Reads every Quiltt Connection of the profile and folds them into one health: the worst classified issue wins (see
    /// <see cref="QuilttIssueMapper.Aggregate"/>). The issue's <c>ProviderCode</c> carries the Quiltt connection id for reconnect
    /// (<see cref="QuilttProviderCode.TryGetConnectionId"/>). Transport/HTTP failures throw typed provider exceptions.
    /// </summary>
    public async Task<ConnectionHealth> GetConnectionHealthAsync(string memberProviderId, CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(memberProviderId, cancellationToken).ConfigureAwait(false);
        var connections = await (_connections ??= _client.ListConnectionsAsync(session.Token, cancellationToken)).ConfigureAwait(false);
        return QuilttIssueMapper.Aggregate([.. connections.Select(c => (c.Id, c.Status))]);
    }

    // ── Accounts ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string memberProviderId, CancellationToken cancellationToken = default)
    {
        var raw = await GetAccountsCoreAsync(memberProviderId, cancellationToken).ConfigureAwait(false);
        var mapped = new List<ProviderAccount>();
        foreach (var qa in raw)
        {
            try
            {
                mapped.Add(QuilttMapper.ToAccount(qa));
            }
            catch (UnknownCurrencyException ex)
            {
                _logger.LogWarning("Skipping Quiltt account: {Message}", ex.Message); // never guess USD (#124)
            }
        }

        foreach (var a in mapped)
        {
            _currencyByAccount[a.ProviderId] = a.CurrencyCode;
        }

        return mapped;
    }

    private async Task<QuilttSession> EnsureSessionAsync(string profileId, CancellationToken ct)
    {
        if (_session is null || !string.Equals(_session.ProfileId, profileId, StringComparison.Ordinal))
        {
            _session = await _client.CreateSessionAsync(profileId, ct).ConfigureAwait(false);
            _accounts = null;
            _connections = null;
            _transactions = null;
        }

        return _session;
    }

    private async Task<IReadOnlyList<QAccount>> GetAccountsCoreAsync(string profileId, CancellationToken ct)
    {
        var session = await EnsureSessionAsync(profileId, ct).ConfigureAwait(false);
        var accounts = await (_accounts ??= _client.ListAccountsAsync(session.Token, ct)).ConfigureAwait(false);

        // Reads keep working on stale data while a connection is broken; refuse to report a "successful" sync of nothing but
        // broken connections so the shared layers record the issue instead of a false recovery.
        var byConnection = accounts
            .Where(a => a.Connection?.Id is not null)
            .GroupBy(a => a.Connection!.Id!)
            .Select(g => (Id: (string?)g.Key, Status: g.First().Connection!.Status))
            .ToList();
        if (QuilttIssueMapper.BlockingException(byConnection) is { } blocked)
        {
            throw blocked;
        }

        return accounts;
    }

    // ── Transactions ─────────────────────────────────────────────────────────

    public async Task<TransactionPage> GetTransactionsAsync(
        string providerAccountId, SyncCursor cursor, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new FatalProviderException(
            "Quiltt accounts must be read before transactions.", "SEQUENCE")
        { Kind = ProviderIssueKind.Internal };

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // A continuation cursor ("c:yyyy-MM-dd") marks a backfill cut at the page bound; it resumes exactly there. Re-applying the
        // overlap would step back over what the previous page covered and never finish.
        var continuing = cursor.Value.StartsWith(ContinuationPrefix, StringComparison.Ordinal);
        var cursorText = continuing ? cursor.Value[ContinuationPrefix.Length..] : cursor.Value;
        var from = cursor.IsInitial || !DateOnly.TryParse(cursorText, out var c)
            ? today.AddDays(-Math.Max(1, _options.InitialHistoryDays))
            : continuing ? c : c.AddDays(-Math.Max(0, _options.OverlapDays));

        // One read serves every account while it is complete; a bounded (truncated) read only serves the window it started at.
        if (_transactions is not { } cached || from < cached.From || (from > cached.From && cached.Task.IsCompletedSuccessfully && cached.Task.Result.Truncated))
        {
            _transactions = (from, _client.ListTransactionsAsync(session.Token, from, cancellationToken));
        }

        var read = await _transactions.Value.Task.ConfigureAwait(false);
        var all = read.Items;
        var fallback = _currencyByAccount.GetValueOrDefault(providerAccountId);

        // The read is cut at MaxPages. Rows are ordered by date, so the last date seen may continue on the next page: deliver only
        // the days before it and resume from there in the next sync-loop iteration instead of failing the whole sync.
        DateOnly? cutAt = null;
        if (read.Truncated)
        {
            var lastDate = all.Where(QuilttMapper.IsIngestible)
                .Select(t => DateOnly.Parse(t.Date!, System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty().Max();
            if (lastDate > from)
            {
                cutAt = lastDate;
            }
            else
            {
                _logger.LogWarning("Quiltt returned {Max} pages inside one day; continuing is not possible, so this pass ends here.", _options.MaxPages);
            }
        }

        var forAccount = all
            .Where(t => string.Equals(t.Account?.Id, providerAccountId, StringComparison.Ordinal))
            .ToList();

        // PROJECTED rows are forecast items, not ledger entries; rows without a date cannot be placed on the ledger.
        var ingestible = forAccount.Where(QuilttMapper.IsIngestible).ToList();
        var skipped = forAccount.Count - ingestible.Count;
        foreach (var bad in forAccount.Except(ingestible))
        {
            _logger.LogWarning("Skipping Quiltt transaction {ProviderTransactionId}: projected or no usable date.", bad.Id);
        }

        var mappedItems = new List<ProviderTransaction>();
        foreach (var raw in ingestible)
        {
            try
            {
                mappedItems.Add(QuilttMapper.ToTransaction(raw, fallback));
            }
            catch (UnknownCurrencyException ex)
            {
                skipped++;
                _logger.LogWarning("Skipping Quiltt transaction: {Message}", ex.Message);
            }
        }

        var items = mappedItems
            .Where(t => t.PostedDate >= from && (cutAt is null || t.PostedDate < cutAt.Value))
            .OrderBy(t => t.PostedDate)
            .ToList();

        if (cutAt is { } cut)
        {
            _logger.LogInformation("Quiltt read was cut at {Max} pages; continuing from {Date}.", _options.MaxPages, cut);
            return new TransactionPage(items, new SyncCursor(ContinuationPrefix + cut.ToString("yyyy-MM-dd")), HasMore: true) { SkippedCount = skipped };
        }

        var settled = items.Where(t => !t.IsPending).Select(t => t.PostedDate).DefaultIfEmpty().Max();
        // Only pending rows so far: everything older than the earliest has been seen, so do not re-read the whole window every sync.
        // A truly empty account keeps its initial cursor so history that arrives later is not skipped.
        var next = settled != default ? new SyncCursor(settled.ToString("yyyy-MM-dd"))
            : cursor.IsInitial && items.Count > 0 ? new SyncCursor(items.Min(t => t.PostedDate).AddDays(-1).ToString("yyyy-MM-dd"))
            : cursor;
        _logger.LogDebug("Quiltt returned {Count} transaction(s) for one account.", items.Count);
        return new TransactionPage(items, next, HasMore: false) { SkippedCount = skipped };
    }

    private const string ContinuationPrefix = "c:";

    // ── Webhooks ─────────────────────────────────────────────────────────────

    public Task VerifyWebhookAsync(ReadOnlyMemory<byte> rawBody, string signatureHeader, CancellationToken cancellationToken = default)
    {
        if (_verifier is null)
        {
            throw new InvalidWebhookSignatureException("Quiltt:WebhookSecret is not configured; webhooks are refused.");
        }

        _verifier.Verify(rawBody.Span, signatureHeader);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Parses a verified delivery: <c>{"eventTypes":[...],"profile":{"id":"p_..."},"events":[{"type":"...","profile":{"id":".."}}]}</c>.
    /// Tolerant of field placement; the first event type and profile id found win.
    /// </summary>
    public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FatalProviderException("Quiltt webhook payload is not a JSON object.");
            }

            string? type = null;
            if (root.TryGetProperty("eventTypes", out var types) && types.ValueKind == JsonValueKind.Array && types.GetArrayLength() > 0)
            {
                type = types[0].GetString();
            }

            JsonElement? firstEvent = root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array && evs.GetArrayLength() > 0
                ? evs[0]
                : null;
            type ??= Str(firstEvent, "type") ?? Str(root, "type");

            var profile = Str(Obj(root, "profile"), "id")
                          ?? Str(Obj(firstEvent, "profile"), "id")
                          ?? Str(root, "profileId")
                          ?? string.Empty;

            if (string.IsNullOrWhiteSpace(type))
            {
                throw new FatalProviderException("Quiltt webhook payload has no event type.");
            }

            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Str(Obj(firstEvent, "record"), "id") is { } recordId)
            {
                meta["recordId"] = recordId;
            }

            return Task.FromResult(new ProviderWebhookEvent(type, profile, DateTimeOffset.UtcNow, meta));
        }
        catch (JsonException ex)
        {
            throw new FatalProviderException("Quiltt webhook payload is not valid JSON.", inner: ex);
        }
    }

    /// <summary>
    /// Whether a webhook event type means new bank data is available. Quiltt recommends prefix matching
    /// (subscribing to <c>connection.synced.successful</c> also delivers its <c>.initial</c>/<c>.historical</c> children).
    /// Events such as <c>profile.created</c> carry no data, so they must not trigger a sync.
    /// </summary>
    public static bool IsSyncTrigger(string? eventType) =>
        eventType is not null
        && (eventType.StartsWith("connection.synced.successful", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("connection.created", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("account.verified", StringComparison.OrdinalIgnoreCase));

    private static JsonElement? Obj(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
