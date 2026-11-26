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
public sealed class MxFinancialDataProvider : IFinancialDataProvider, IMemberIdResolver
{
    private readonly MxApiClient _client;
    private readonly MxWebhookVerifier _webhookVerifier;
    private readonly MxProviderOptions _options;
    private readonly ILogger<MxFinancialDataProvider> _logger;
    // Account currency by packed account id, so a transaction without its own currency inherits it instead of defaulting to USD (#124).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _currencyByAccount = new(StringComparer.Ordinal);

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

    /// <summary>Member guid placeholder stored until the Connect widget has created the real member.</summary>
    internal const string PendingMember = "PENDING";

    /// <summary>
    /// Resolves the PENDING placeholder to the real member created by the Connect widget.
    /// Returns null when the user has not completed the widget yet.
    /// </summary>
    private async Task<string?> ResolveMemberGuidAsync(string userGuid, string memberGuid, CancellationToken ct)
    {
        if (!string.Equals(memberGuid, PendingMember, StringComparison.Ordinal))
        {
            return memberGuid;
        }

        var members = await _client.ListMembersAsync(userGuid, ct).ConfigureAwait(false);
        return members.FirstOrDefault(m => m.Guid is not null)?.Guid;
    }

    public async Task<string?> TryResolveMemberProviderIdAsync(string memberProviderId, CancellationToken cancellationToken = default)
    {
        var (userGuid, guid) = MxCompositeId.Unpack(memberProviderId);
        if (!string.Equals(guid, PendingMember, StringComparison.Ordinal))
        {
            return null;
        }

        var resolved = await ResolveMemberGuidAsync(userGuid, guid, cancellationToken).ConfigureAwait(false);
        return resolved is null ? null : MxCompositeId.Pack(userGuid, resolved);
    }

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Accounts |
        ProviderCapabilities.IncrementalTransactions |
        ProviderCapabilities.FullTransactionHistory |
        ProviderCapabilities.WebhookVerification |
        ProviderCapabilities.WebhookParsing;

    /// <summary>Operator-held billable API (household tenant only); balances already report what is owed.</summary>
    public const ConnectorTraits DeclaredTraits = ConnectorTraits.BillablePerCall;

    public ConnectorTraits Traits => DeclaredTraits;

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

        // ProvisionMxConnectionAsync guarantees a non-null widget URL when fetchWidgetUrl is true
        // (it throws FatalProviderException otherwise). Assert here so the contract is explicit
        // rather than relying on a null-forgiving operator.
        if (widgetUrl is null)
        {
            throw new FatalProviderException("MX widget URL was unexpectedly null after provisioning.");
        }

        return (memberRef, widgetUrl);
    }

    /// <summary>
    /// Returns a Connect widget URL for an already-provisioned member WITHOUT creating another
    /// MX user or member (only the widget call is made).
    /// </summary>
    public async Task<string> GetConnectWidgetUrlForMemberAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, _) = MxCompositeId.Unpack(memberProviderId);
        var widget = await _client.GetConnectWidgetUrlAsync(userGuid, cancellationToken).ConfigureAwait(false);
        return widget.Url ?? throw new FatalProviderException("MX widget response contained no URL.");
    }

    /// <summary>
    /// Core provisioning: create/find the MX user, discover the member, and optionally obtain
    /// the Connect widget URL. Both public begin-connection methods delegate here so the MX API
    /// calls and status-mapping logic stay in one place (DRY).
    ///
    /// Members are NEVER created server-side (MX's POST /members requires the user's bank
    /// credentials, and rejects the call when the Connect widget already created the member).
    /// </summary>
    /// <param name="tenantId">Tenant for whom the connection is provisioned.</param>
    /// <param name="fetchWidgetUrl">
    ///   When <see langword="true"/> (widget path), adopts an existing widget-created member or
    ///   returns a PENDING placeholder, and calls <c>GetConnectWidgetUrlAsync</c>. When
    ///   <see langword="false"/> (sync path), discovers the widget-created member via
    ///   <c>GET /users/{guid}/members</c> and waits for it to finish aggregating
    ///   (see <see cref="DiscoverAndWaitForReadyMemberAsync"/>); skips the billable widget call.
    /// </param>
    /// <returns>
    ///   The <see cref="ProviderMemberRef"/> and, when
    ///   <paramref name="fetchWidgetUrl"/> is <see langword="true"/>, the Connect widget URL
    ///   (otherwise <see langword="null"/>).
    /// </returns>
    private async Task<(ProviderMemberRef MemberRef, string? WidgetUrl)> ProvisionMxConnectionAsync(
        TenantId tenantId,
        bool fetchWidgetUrl,
        CancellationToken cancellationToken)
    {
        // Recovery-first: every step looks for what a previous (possibly half-failed) attempt already
        // created before POSTing, so a crash between CreateUser and CreateMember can never strand the
        // tenant or leave a second user/member behind. POSTs are also never auto-retried.
        // 1. Ensure an MX user exists for this tenant (idempotent external id).
        var externalId = $"flowledger-{tenantId.Value:N}";
        var user = await _client.FindUserByExternalIdAsync(externalId, cancellationToken).ConfigureAwait(false)
                   ?? await _client.CreateUserAsync(externalId, cancellationToken).ConfigureAwait(false);
        var userGuid = user.Guid
            ?? throw new FatalProviderException("MX returned a user without a guid.");

        if (!fetchWidgetUrl)
        {
            // 2. Sync path: discover the member the Connect widget created and wait until it is
            //    ready. No members / CHALLENGED / terminal / poll cap → NeedsUserAction (not fatal).
            var syncRef = await DiscoverAndWaitForReadyMemberAsync(userGuid, cancellationToken)
                .ConfigureAwait(false);
            return (syncRef, null);
        }

        // 2. Widget path: members cannot be created server-side: the Connect widget (step 3)
        //    collects credentials and creates the member. Adopt a member if the widget already
        //    produced one; otherwise return a PENDING placeholder that is resolved to the real
        //    member the first time accounts/status are read.
        var existingMembers = await _client.ListMembersAsync(userGuid, cancellationToken).ConfigureAwait(false);
        var member = existingMembers.FirstOrDefault(m => m.Guid is not null);
        var memberGuid = member?.Guid ?? PendingMember;

        // 3. Obtain the Connect widget URL (billable call — only requested on the widget path).
        var widget = await _client
            .GetConnectWidgetUrlAsync(userGuid, cancellationToken).ConfigureAwait(false);
        var widgetUrl = widget.Url
            ?? throw new FatalProviderException("MX widget response contained no URL.");

        var institution = string.IsNullOrWhiteSpace(member?.Name) ? "MX Institution" : member!.Name!;

        // A freshly provisioned member is pending until the user completes Connect, unless MX
        // already reports it connected (e.g. test/sandbox auto-connect).
        var status = member is null ? ConnectionStatus.ConnectionPending : MxMapper.ToConnectionStatus(member.ConnectionStatus);
        var refStatus = status == ConnectionStatus.Healthy
            ? ConnectionStatus.Connected
            : ConnectionStatus.ConnectionPending;

        var memberRef = new ProviderMemberRef(
            MxCompositeId.Pack(userGuid, memberGuid),
            institution,
            refStatus);

        return (memberRef, widgetUrl);
    }

    // ── Member readiness (post-widget sync) ─────────────────────────────────────

    private enum MemberReadiness
    {
        /// <summary>Connected (or degraded) and not aggregating — accounts can be read.</summary>
        Ready,
        /// <summary>CHALLENGED: MFA / extra verification needed in the Connect widget.</summary>
        Challenged,
        /// <summary>Dead-end status (needs reconnect, error, disabled).</summary>
        Terminal,
        /// <summary>Still connecting or aggregating — poll again.</summary>
        Transient,
    }

    private const string ChallengedStatus = "CHALLENGED";

    /// <summary>
    /// Classifies a member via <see cref="MxMapper.ToConnectionStatus"/> (single source of truth
    /// for MX status semantics) plus the <c>is_being_aggregated</c> flag.
    /// </summary>
    private static MemberReadiness Classify(MxMember member)
    {
        if (string.Equals(member.ConnectionStatus?.Trim(), ChallengedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return MemberReadiness.Challenged;
        }

        return MxMapper.ToConnectionStatus(member.ConnectionStatus) switch
        {
            ConnectionStatus.NeedsUserAction or ConnectionStatus.Error or ConnectionStatus.Disabled
                => MemberReadiness.Terminal,
            ConnectionStatus.Healthy or ConnectionStatus.Degraded when member.IsBeingAggregated != true
                => MemberReadiness.Ready,
            _ => MemberReadiness.Transient,
        };
    }

    /// <summary>
    /// Discovers widget-created members for <paramref name="userGuid"/> via
    /// <c>GET /users/{guid}/members</c>, gates on their aggregation status, and returns a
    /// <see cref="ProviderMemberRef"/> for the first READY member, polling the status endpoint for
    /// a member that is still aggregating. Throws <see cref="NeedsUserActionProviderException"/>
    /// (never <see cref="FatalProviderException"/>) for the non-fatal conditions: no members,
    /// CHALLENGED, terminal, still connecting after <see cref="MxProviderOptions.MaxPollAttempts"/>.
    /// </summary>
    private async Task<ProviderMemberRef> DiscoverAndWaitForReadyMemberAsync(
        string userGuid, CancellationToken ct)
    {
        var members = (await _client.ListMembersAsync(userGuid, ct).ConfigureAwait(false))
            .Where(m => m.Guid is not null)
            .ToList();

        if (members.Count == 0)
        {
            throw new NeedsUserActionProviderException(
                "No financial institution connected yet. Use Connect to add one.",
                userHint: "Connect a bank account using the Connect button.",
                providerCode: "NoMembers")
            {
                // Nothing is wrong with a credential: the user simply has not linked an institution yet.
                Kind = ProviderIssueKind.Disconnected,
            };
        }

        var ready = members.FirstOrDefault(m => Classify(m) == MemberReadiness.Ready);
        if (ready is not null)
        {
            return BuildMemberRef(userGuid, ready);
        }

        var transient = members.FirstOrDefault(m => Classify(m) == MemberReadiness.Transient);
        if (transient is null)
        {
            // Every member is challenged or terminal.
            ThrowForUnusableMember(members.FirstOrDefault(m => Classify(m) == MemberReadiness.Challenged)
                                   ?? members[0]);
        }

        var readyMember = await WaitForMemberReadyAsync(userGuid, transient!, ct).ConfigureAwait(false);
        return BuildMemberRef(userGuid, readyMember);
    }

    /// <summary>
    /// Polls <c>GET /users/{u}/members/{m}/status</c> until the member is ready, up to
    /// <see cref="MxProviderOptions.MaxPollAttempts"/> times, <see cref="MxProviderOptions.PollIntervalMs"/>
    /// apart. Returns immediately (no call) when <paramref name="member"/> is already ready.
    /// </summary>
    private async Task<MxMember> WaitForMemberReadyAsync(string userGuid, MxMember member, CancellationToken ct)
    {
        switch (Classify(member))
        {
            case MemberReadiness.Ready:
                return member;
            case MemberReadiness.Challenged:
            case MemberReadiness.Terminal:
                ThrowForUnusableMember(member);
                break;
        }

        var memberGuid = member.Guid ?? throw new FatalProviderException("MX member has no guid.");
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(0, _options.PollIntervalMs));
        var maxAttempts = Math.Max(1, _options.MaxPollAttempts);

        _logger.LogInformation(
            "MX member {MemberGuid} is aggregating or transient (status={Status}, isAggregating={IsAgg}). " +
            "Polling status up to {MaxAttempts} times with {IntervalMs}ms interval.",
            memberGuid, member.ConnectionStatus, member.IsBeingAggregated, maxAttempts, _options.PollIntervalMs);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }

            var current = await _client.GetMemberStatusAsync(userGuid, memberGuid, ct).ConfigureAwait(false);
            switch (Classify(current))
            {
                case MemberReadiness.Ready:
                    _logger.LogInformation("MX member {MemberGuid} is ready after {Attempt} poll(s).", memberGuid, attempt);
                    // The status endpoint may omit name; keep the listed member's identity fields.
                    return current with
                    {
                        Guid = current.Guid ?? memberGuid,
                        Name = string.IsNullOrWhiteSpace(current.Name) ? member.Name : current.Name,
                    };
                case MemberReadiness.Challenged:
                case MemberReadiness.Terminal:
                    ThrowForUnusableMember(current);
                    break;
            }

            _logger.LogDebug(
                "MX member {MemberGuid} poll {Attempt}/{MaxAttempts}: status={Status}, isAggregating={IsAgg}.",
                memberGuid, attempt, maxAttempts, current.ConnectionStatus, current.IsBeingAggregated);
        }

        // The member is healthy, just not finished aggregating. That is retryable, not a reason to ask the user to act.
        throw new TransientProviderException(
            "Account is still connecting; data will appear shortly.",
            retryAfter: TimeSpan.FromMinutes(1),
            providerCode: "StillAggregating")
        {
            Kind = ProviderIssueKind.ProviderUnavailable,
        };
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowForUnusableMember(MxMember member)
    {
        if (Classify(member) == MemberReadiness.Challenged)
        {
            throw new NeedsUserActionProviderException(
                "Additional verification needed. Please reconnect to continue.",
                userHint: "Your bank requires additional verification. Re-open the Connect widget to complete it.",
                providerCode: ChallengedStatus);
        }

        throw new NeedsUserActionProviderException(
            "Connection needs to be re-established. Please reconnect.",
            userHint: member.ConnectionStatusMessage ?? "Your bank connection was lost. Please reconnect your account.",
            providerCode: member.ConnectionStatus ?? "Terminal");
    }

    private static ProviderMemberRef BuildMemberRef(string userGuid, MxMember member)
    {
        var memberGuid = member.Guid
            ?? throw new FatalProviderException("MX member has no guid.");
        var institution = string.IsNullOrWhiteSpace(member.Name) ? "MX Institution" : member.Name;
        var status = MxMapper.ToConnectionStatus(member.ConnectionStatus) == ConnectionStatus.Degraded
            ? ConnectionStatus.Degraded
            : ConnectionStatus.Connected;
        return new ProviderMemberRef(
            MxCompositeId.Pack(userGuid, memberGuid),
            institution,
            status);
    }

    public async Task<ConnectionStatus> GetConnectionStatusAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, memberGuid) = MxCompositeId.Unpack(memberProviderId);
        var resolved = await ResolveMemberGuidAsync(userGuid, memberGuid, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return ConnectionStatus.ConnectionPending;
        }

        var member = await _client
            .GetMemberStatusAsync(userGuid, resolved, cancellationToken)
            .ConfigureAwait(false);

        return MxMapper.ToConnectionStatus(member.ConnectionStatus);
    }

    // ── Accounts ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, pendingOrMemberGuid) = MxCompositeId.Unpack(memberProviderId);
        var memberGuid = await ResolveMemberGuidAsync(userGuid, pendingOrMemberGuid, cancellationToken).ConfigureAwait(false)
            ?? throw new NeedsUserActionProviderException(
                "No MX member exists yet: complete the Connect widget (POST /api/integrations/mx/connect-token) first.",
                userHint: "Finish connecting your institution in the Connect widget.");

        // Guard: a member needing user action cannot serve account data. This is an intentional
        // defence-in-depth check: a fresh GetMemberStatus call costs one MX API round-trip but
        // avoids returning stale accounts for a member that has been disconnected or challenged
        // since the last sync. The trade-off is accepted — the safety benefit outweighs the cost.
        var member = await _client
            .GetMemberStatusAsync(userGuid, memberGuid, cancellationToken)
            .ConfigureAwait(false);

        // First sync after a Connect-widget flow (the persisted id was still the PENDING placeholder):
        // the widget-created member may still be aggregating. Wait for it (bounded by
        // MaxPollAttempts) instead of reading a half-built account list; CHALLENGED / terminal /
        // poll-cap surface as NeedsUserAction. Steady-state syncs (real member id) are unaffected.
        if (string.Equals(pendingOrMemberGuid, PendingMember, StringComparison.Ordinal))
        {
            member = await WaitForMemberReadyAsync(
                userGuid, member with { Guid = member.Guid ?? memberGuid }, cancellationToken).ConfigureAwait(false);
        }

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
                ProviderAccount dto;
                try
                {
                    dto = MxMapper.ToProviderAccount(a);
                }
                catch (UnknownCurrencyException ex)
                {
                    // Never guess USD for an account whose currency the provider did not report (#124).
                    _logger.LogWarning("Skipping MX account: {Message}", ex.Message);
                    continue;
                }

                // Re-pack the account id with the owning user so GetTransactions can address it.
                var packedId = MxCompositeId.Pack(userGuid, dto.ProviderId);
                _currencyByAccount[packedId] = dto.CurrencyCode;
                results.Add(dto with { ProviderId = packedId });
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
        var position = MxCursor.Decode(cursor, effectivePageSize, _logger);

        var response = await _client
            .GetTransactionsAsync(userGuid, accountGuid, position.Page, position.RecordsPerPage, cancellationToken, position.FromDate)
            .ConfigureAwait(false);

        var accountCurrency = _currencyByAccount.GetValueOrDefault(providerAccountId);
        var mapped = new List<ProviderTransaction>();
        var unknownCurrency = 0;
        foreach (var raw in response.Transactions ?? [])
        {
            try
            {
                mapped.Add(MxMapper.ToProviderTransaction(raw, _logger, accountCurrency) with { ProviderAccountId = providerAccountId });
            }
            catch (UnknownCurrencyException ex)
            {
                unknownCurrency++;
                _logger.LogWarning("Skipping MX transaction: {Message}", ex.Message);
            }
        }

        // A row with no parseable date maps to DateOnly.MinValue (year 0001); never persist that sentinel.
        var items = mapped.Where(t => t.PostedDate != DateOnly.MinValue).ToList();
        var skipped = mapped.Count - items.Count + unknownCurrency;
        foreach (var bad in mapped.Where(t => t.PostedDate == DateOnly.MinValue))
        {
            _logger.LogWarning("Skipping MX transaction {ProviderTransactionId}: no usable date.", bad.ProviderId);
        }

        // Without pagination metadata a full page may be truncated: keep paging (a short page ends the pass) so the
        // date watermark is only written once the pass really completed.
        var hasMore = response.Pagination is null
            ? (response.Transactions?.Count ?? 0) >= position.RecordsPerPage
            : HasMorePages(response.Pagination, position.Page);

        // Bound a runaway pass: stop here WITHOUT a watermark and resume from the next page on the next sync, so a
        // truncated pass never moves the date watermark past unread history.
        if (hasMore && position.Page >= Math.Max(1, _options.MaxPages))
        {
            _logger.LogWarning(
                "GetTransactionsAsync: reached MaxPages limit ({MaxPages}); resuming on the next sync.",
                _options.MaxPages);
            return new TransactionPage(items, position.Next().Encode(), HasMore: false) { SkippedCount = skipped };
        }

        // Mid-pass: advance to the next page (same date window) so a crash resumes here.
        // End of pass: persist a date WATERMARK (page 1, from = today - overlap), never "last page + 1".
        // MX is newest-first, so a parked page cursor would silently skip everything posted later.
        var nextCursor = hasMore
            ? position.Next().Encode()
            : MxCursor.Watermark(
                position.RecordsPerPage,
                DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-Math.Max(0, _options.IncrementalOverlapDays))).Encode();

        return new TransactionPage(items, nextCursor, hasMore) { SkippedCount = skipped };
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
        MxWebhookPayload? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize(
                rawBody.Span, MxJsonContext.Default.MxWebhookPayload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(ex, "MX webhook payload is malformed and could not be parsed.");
            throw new FatalProviderException(
                "MX webhook payload is malformed and could not be parsed.", inner: ex);
        }

        if (payload is null)
        {
            throw new FatalProviderException("MX webhook payload could not be parsed.");
        }

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
