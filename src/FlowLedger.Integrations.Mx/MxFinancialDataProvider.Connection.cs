using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Contracts;
using FlowLedger.Integrations.Mx.Mapping;

namespace FlowLedger.Integrations.Mx;

// Connection lifecycle and member readiness (#103: split out of MxFinancialDataProvider.cs).
public sealed partial class MxFinancialDataProvider
{
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

        LogMemberPolling(memberGuid, member.ConnectionStatus, member.IsBeingAggregated, maxAttempts, _options.PollIntervalMs);

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
                    LogMemberReady(memberGuid, attempt);
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

            LogMemberPollAttempt(memberGuid, attempt, maxAttempts, current.ConnectionStatus, current.IsBeingAggregated);
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
}
