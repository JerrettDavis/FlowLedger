using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Contracts;
using FlowLedger.Integrations.Mx.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Real MX (mx.com) implementation of <see cref="IFinancialDataProvider"/>.
///
/// Delegates all wire concerns to <see cref="MxApiClient"/> and all shape translation to
/// <see cref="MxMapper"/> / <see cref="MxCursor"/>. Connection lifecycle, account and
/// transaction fetch, and webhook verify/parse are implemented against the MX Platform API.
///
/// Identity packing: MX scopes accounts/transactions by user_guid, but the contract passes a
/// single opaque id. The member ref's <c>ProviderId</c> is <c>{userGuid}|{memberGuid}</c> and each
/// account's <c>ProviderId</c> is <c>{userGuid}|{accountGuid}</c> (see <see cref="MxCompositeId"/>).
/// </summary>
public sealed class MxFinancialDataProvider : IFinancialDataProvider
{
    private readonly MxApiClient _client;
    private readonly MxWebhookVerifier _webhookVerifier;
    private readonly MxProviderOptions _options;
    private readonly ILogger<MxFinancialDataProvider> _logger;

    internal MxFinancialDataProvider(
        MxApiClient client,
        MxWebhookVerifier webhookVerifier,
        IOptions<MxProviderOptions> options,
        ILogger<MxFinancialDataProvider> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _webhookVerifier = webhookVerifier ?? throw new ArgumentNullException(nameof(webhookVerifier));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Metadata ───────────────────────────────────────────────────────────────

    public string ProviderName => "MX";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Accounts |
        ProviderCapabilities.IncrementalTransactions |
        ProviderCapabilities.FullTransactionHistory |
        ProviderCapabilities.WebhookVerification |
        ProviderCapabilities.WebhookParsing;

    // ── Connection lifecycle ─────────────────────────────────────────────────────

    public async Task<ProviderMemberRef> BeginConnectionAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        var (memberRef, _) = await ProvisionMxConnectionAsync(
            tenantId, fetchWidgetUrl: false, cancellationToken).ConfigureAwait(false);
        return memberRef;
    }

    /// <summary>
    /// Begins a connection and also returns the Connect widget URL — used by the
    /// <c>/api/integrations/mx/connect-token</c> endpoint.
    /// </summary>
    public async Task<(ProviderMemberRef Member, string WidgetUrl)> BeginConnectionWithWidgetAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        var (memberRef, widgetUrl) = await ProvisionMxConnectionAsync(
            tenantId, fetchWidgetUrl: true, cancellationToken).ConfigureAwait(false);
        return (memberRef, widgetUrl!);
    }

    /// <summary>
    /// Core provisioning: create/find the MX user, provision a member, and optionally obtain
    /// the Connect widget URL. Both public begin-connection methods delegate here so the three
    /// MX API calls and status-mapping logic stay in one place (DRY).
    /// </summary>
    /// <param name="tenantId">Tenant for whom the connection is provisioned.</param>
    /// <param name="fetchWidgetUrl">
    ///   When <see langword="true"/>, calls <c>GetConnectWidgetUrlAsync</c> and returns the URL;
    ///   when <see langword="false"/>, skips the billable widget call (widget URL only needed
    ///   by <c>/api/integrations/mx/connect-token</c>).
    /// </param>
    /// <returns>
    ///   The provisioned <see cref="ProviderMemberRef"/> and, when
    ///   <paramref name="fetchWidgetUrl"/> is <see langword="true"/>, the Connect widget URL
    ///   (otherwise <see langword="null"/>).
    /// </returns>
    private async Task<(ProviderMemberRef MemberRef, string? WidgetUrl)> ProvisionMxConnectionAsync(
        TenantId tenantId,
        bool fetchWidgetUrl,
        CancellationToken cancellationToken)
    {
        // 1. Ensure an MX user exists for this tenant (idempotent external id).
        var externalId = $"flowledger-{tenantId.Value:N}";
        var user = await _client.CreateUserAsync(externalId, cancellationToken).ConfigureAwait(false);
        var userGuid = user.Guid
            ?? throw new FatalProviderException("MX CreateUser returned a user without a guid.");

        // 2. Provision a member for the institution.
        var member = await _client
            .CreateMemberAsync(userGuid, _options.DefaultInstitutionCode, cancellationToken)
            .ConfigureAwait(false);
        var memberGuid = member.Guid
            ?? throw new FatalProviderException("MX CreateMember returned a member without a guid.");

        // 3. Optionally obtain the Connect widget URL (billable call — only request when needed).
        string? widgetUrl = null;
        if (fetchWidgetUrl)
        {
            var widget = await _client
                .GetConnectWidgetUrlAsync(userGuid, cancellationToken).ConfigureAwait(false);
            widgetUrl = widget.Url
                ?? throw new FatalProviderException("MX widget response contained no URL.");
        }

        var institution = string.IsNullOrWhiteSpace(member.Name) ? "MX Institution" : member.Name;

        // A freshly provisioned member is pending until the user completes Connect, unless MX
        // already reports it connected (e.g. test/sandbox auto-connect).
        var status = MxMapper.ToConnectionStatus(member.ConnectionStatus);
        var refStatus = status == ConnectionStatus.Healthy
            ? ConnectionStatus.Connected
            : ConnectionStatus.ConnectionPending;

        var memberRef = new ProviderMemberRef(
            MxCompositeId.Pack(userGuid, memberGuid),
            institution,
            refStatus);

        return (memberRef, widgetUrl);
    }

    public async Task<ConnectionStatus> GetConnectionStatusAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, memberGuid) = MxCompositeId.Unpack(memberProviderId);
        var member = await _client
            .GetMemberStatusAsync(userGuid, memberGuid, cancellationToken)
            .ConfigureAwait(false);

        return MxMapper.ToConnectionStatus(member.ConnectionStatus);
    }

    // ── Accounts ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, memberGuid) = MxCompositeId.Unpack(memberProviderId);

        // Guard: a member needing user action cannot serve account data. This is an intentional
        // defence-in-depth check: a fresh GetMemberStatus call costs one MX API round-trip but
        // avoids returning stale accounts for a member that has been disconnected or challenged
        // since the last sync. The trade-off is accepted — the safety benefit outweighs the cost.
        var member = await _client
            .GetMemberStatusAsync(userGuid, memberGuid, cancellationToken)
            .ConfigureAwait(false);
        if (MxMapper.RequiresUserAction(member.ConnectionStatus))
        {
            throw new NeedsUserActionProviderException(
                $"MX member {memberGuid} requires user action (status {member.ConnectionStatus}).",
                userHint: member.ConnectionStatusMessage,
                providerCode: member.ConnectionStatus);
        }

        var results = new List<ProviderAccount>();
        var page = 1;
        var recordsPerPage = _options.RecordsPerPage;
        var maxPages = _options.MaxPages;

        while (true)
        {
            var response = await _client
                .GetAccountsAsync(userGuid, memberGuid, page, recordsPerPage, cancellationToken)
                .ConfigureAwait(false);

            foreach (var a in response.Accounts ?? [])
            {
                var dto = MxMapper.ToProviderAccount(a);
                // Re-pack the account id with the owning user so GetTransactions can address it.
                results.Add(dto with { ProviderId = MxCompositeId.Pack(userGuid, dto.ProviderId) });
            }

            if (!HasMorePages(response.Pagination, page))
            {
                break;
            }

            if (page >= maxPages)
            {
                _logger.LogWarning(
                    "GetAccountsAsync: reached MaxPages limit ({MaxPages}) for member {MemberGuid}. " +
                    "Returning {Count} accounts collected so far. Remaining pages skipped.",
                    maxPages, memberGuid, results.Count);
                break;
            }

            page++;
        }

        return results;
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<TransactionPage> GetTransactionsAsync(
        string providerAccountId,
        SyncCursor cursor,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, accountGuid) = MxCompositeId.Unpack(providerAccountId);

        // Cursor encodes (page, records_per_page). pageSize from the caller seeds an initial cursor.
        var effectivePageSize = pageSize > 0 ? pageSize : _options.RecordsPerPage;
        var position = MxCursor.Decode(cursor, effectivePageSize);

        var response = await _client
            .GetTransactionsAsync(userGuid, accountGuid, position.Page, position.RecordsPerPage, cancellationToken)
            .ConfigureAwait(false);

        var items = (response.Transactions ?? [])
            .Select(MxMapper.ToProviderTransaction)
            .Select(t => t with { ProviderAccountId = providerAccountId })
            .ToList();

        var hasMore = HasMorePages(response.Pagination, position.Page);

        // Always advance the cursor to the next page, even on the final page: the persisted
        // cursor represents the next position to fetch on a future incremental sync, and the
        // contract requires the cursor to keep advancing between successive pages.
        var nextCursor = position.Next().Encode();

        return new TransactionPage(items, nextCursor, hasMore);
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────

    public Task VerifyWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        string signatureHeader,
        CancellationToken cancellationToken = default)
    {
        _webhookVerifier.Verify(rawBody.Span, signatureHeader);
        return Task.CompletedTask;
    }

    public Task<ProviderWebhookEvent> ParseWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        CancellationToken cancellationToken = default)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize(
            rawBody.Span, MxJsonContext.Default.MxWebhookPayload)
            ?? throw new FatalProviderException("MX webhook payload could not be parsed.");

        var memberId = payload.MemberGuid ?? payload.Member?.Guid ?? string.Empty;
        var eventType = FirstNonEmpty(payload.Type, payload.Action) ?? "UNKNOWN";
        var occurredAt = payload.CompletedAt is { } epoch
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : DateTimeOffset.UtcNow;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(metadata, "action", payload.Action);
        AddIfPresent(metadata, "user_guid", payload.UserGuid ?? payload.Member?.UserGuid);
        AddIfPresent(metadata, "account_guid", payload.AccountGuid);
        AddIfPresent(metadata, "connection_status",
            payload.ConnectionStatus ?? payload.Member?.ConnectionStatus);
        AddIfPresent(metadata, "completed_on", payload.CompletedOn);

        return Task.FromResult(new ProviderWebhookEvent(eventType, memberId, occurredAt, metadata));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool HasMorePages(MxPagination? pagination, int currentPage)
    {
        if (pagination?.TotalPages is { } total)
        {
            var current = pagination.CurrentPage ?? currentPage;
            return current < total;
        }

        // No pagination metadata → assume this was the only page.
        return false;
    }

    private static void AddIfPresent(IDictionary<string, string> map, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            map[key] = value;
        }
    }

    private static string? FirstNonEmpty(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a))
        {
            return a;
        }

        return string.IsNullOrWhiteSpace(b) ? null : b;
    }
}
