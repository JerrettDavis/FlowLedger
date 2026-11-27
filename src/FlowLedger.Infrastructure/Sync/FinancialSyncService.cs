using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Infrastructure implementation of <see cref="IFinancialSyncService"/>.
///
/// Provider selection is handled at DI registration time in DependencyInjection.cs.
/// This service is unaware of which provider is active — it simply calls
/// <see cref="IFinancialDataProvider"/>.
///
/// Idempotency: transactions are deduplicated by <see cref="TransactionFingerprint"/>.
/// The fingerprint index on the database is a secondary guard; the service also performs
/// an in-memory pre-check against the existing fingerprint set to avoid unnecessary inserts.
///
/// Cursor persistence: cursors are durably stored per tenant, provider, and provider account
/// via <see cref="ISyncCursorStore"/>. The cursor is persisted after each successful page so
/// that a mid-sync crash resumes from the last committed page rather than re-fetching everything.
/// </summary>
internal sealed partial class FinancialSyncService : IFinancialSyncService
{
    private readonly IFinancialDataProvider _provider;
    private readonly FlowLedgerDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ISyncCursorStore _cursorStore;
    private readonly ILogger<FinancialSyncService> _logger;
    private readonly Guid? _householdTenantId;
    private readonly IAccountConnectorLinkStore? _links;
    private readonly IProviderAccountCatalog? _catalog;
    private readonly bool _autoCreateAccounts;
    private readonly IProviderConnectionStore? _health;
    private readonly TimeProvider? _clock;
    private readonly SyncChangeSet _changed = new();   // rows this sync inserted or settled (#163)

    /// <summary>Per-tenant stored-transaction ceiling the provider path enforces; tests lower it.</summary>
    internal int MaxTransactionsPerTenant { get; init; } = TransactionQuota.MaxPerTenant;

    /// <param name="configuration">
    /// When supplied (always, via DI), real providers are restricted to the configured household (legacy) tenant:
    /// their credentials (e.g. the operator's single SimpleFIN access URL) belong to that tenant only. Tests that
    /// construct the service directly may omit it.
    /// </param>
    public FinancialSyncService(
        IFinancialDataProvider provider,
        FlowLedgerDbContext db,
        ITenantContext tenant,
        ISyncCursorStore cursorStore,
        ILogger<FinancialSyncService> logger,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        IAccountConnectorLinkStore? links = null,
        IProviderAccountCatalog? catalog = null,
        IProviderConnectionStore? health = null,
        TimeProvider? clock = null)
    {
        _clock = clock;
        _health = health;
        _links = links;
        _catalog = catalog;
        // Connectors:<id>:AutoCreateAccounts (default true): when false, provider accounts that match no existing account are only
        // catalogued; the owner links them to an account by hand.
        _autoCreateAccounts = !(bool.TryParse(configuration?[$"Connectors:{provider.ProviderName}:AutoCreateAccounts"], out var autoCreate) && !autoCreate);
        _provider = provider;
        _db = db;
        _tenant = tenant;
        _cursorStore = cursorStore;
        _logger = logger;
        _householdTenantId = configuration is null ? null : TenancySettings.Legacy(configuration).LegacyTenantId;
    }

    /// <summary>
    /// Defense in depth behind the DI guard: operator-configured provider credentials must never be used for
    /// (or provisioned into) any tenant other than the household tenant, or one user's bank data leaks to another.
    /// </summary>
    private void EnsureTenantMayUseProvider()
    {
        if (_householdTenantId is { } household
            && !_provider.Traits.HasFlag(ConnectorTraits.PerTenantAccess) // e.g. Quiltt keeps one profile per tenant
            && _tenant.TenantId != household)
        {
            throw new NeedsUserActionProviderException(
                $"The configured {_provider.ProviderName} connection belongs to the household tenant; it cannot be used for this tenant.",
                "Per-user bank linking is not available yet. Add accounts manually or import a CSV.");
        }
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        EnsureTenantMayUseProvider();
        var tenantId = TenantId.From(_tenant.TenantId);
        var record = await GetOrProvisionConnectionAsync(tenantId, ct);
        return record.MemberProviderId;
    }

    /// <summary>
    /// Returns the persisted provider connection for this tenant, provisioning one (a billable
    /// provider call) ONLY if none exists. Re-provisioning on every sync would create an orphan
    /// provider user/member per run.
    /// </summary>
    private async Task<ProviderConnectionRecord> GetOrProvisionConnectionAsync(
        TenantId tenantId, CancellationToken ct, bool allowProvision = true)
    {
        var existing = await _db.ProviderConnections
            .FirstOrDefaultAsync(c => c.ProviderName == _provider.ProviderName, ct);
        if (existing is not null)
        {
            return existing;
        }

        if (!allowProvision)
        {
            // Unattended syncs (scheduler, webhooks) must never create billable provider resources.
            throw new NeedsUserActionProviderException(
                $"No {_provider.ProviderName} connection exists for this tenant. Connect an institution first " +
                "(POST /api/integrations/mx/connect-token); syncs never provision implicitly for real providers.");
        }

        LogProvisioningProviderConnectionForTenant(_logger, tenantId, _provider.ProviderName);
        var memberRef = await _provider.BeginConnectionAsync(tenantId, ct);

        // Persist immediately: if the process dies after this point the member is never re-created.
        var record = ProviderConnectionRecord.Create(
            _tenant.TenantId, _provider.ProviderName, memberRef.ProviderId,
            memberRef.InstitutionName, memberRef.Status.ToString(), DateTimeOffset.UtcNow);
        await _db.ProviderConnections.AddAsync(record, ct);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the sync and records the connection's health (ADR 0018): a classified <see cref="ProviderIssue"/> when the provider
    /// fails (the original exception is then rethrown untouched), or recovery (clears any issue) when it succeeds.
    /// </summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        using var conflicts = new SyncConflictResolver(_db, _logger).Install(); // a user edit mid-sync neither fails it nor is overwritten (#157)
        SyncResult result;
        try
        {
            result = await SyncCoreAsync(ct);
        }
        catch (ProviderException ex)
        {
            await RecordHealthSafelyAsync(ProviderIssueMapping.ToInfo(ProviderIssue.FromException(ex), DateTimeOffset.UtcNow), CancellationToken.None);
            throw; // never swallow: callers (UI, jobs) still see the original failure
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unexpected failure (a database constraint, a bug) must still leave a trail: record it as Internal so the
            // retry policy bounds the attempts (MaxBoundedFailures) instead of repeating it forever with no UI issue.
            await RecordHealthSafelyAsync(ProviderIssueMapping.ToInfo(
                ProviderIssue.Of(ProviderIssueKind.Internal, $"Unexpected {ex.GetType().Name} during sync."), DateTimeOffset.UtcNow), CancellationToken.None);
            throw;
        }

        await RecordHealthSafelyAsync(await ReadProviderHealthAsync(ct), ct);
        return result;
    }

    /// <summary>
    /// A read can succeed against a connection that still needs repair (stale data), so for connectors that can report real
    /// per-connection health (<see cref="ProviderCapabilities.ConnectionHealth"/>) ask them; otherwise a clean sync means recovery (null).
    /// </summary>
    private async Task<ConnectionIssueInfo?> ReadProviderHealthAsync(CancellationToken ct)
    {
        if (!_provider.Capabilities.HasFlag(ProviderCapabilities.ConnectionHealth))
        {
            return null;
        }

        try
        {
            var member = await _db.ProviderConnections
                .Where(c => c.ProviderName == _provider.ProviderName)
                .Select(c => c.MemberProviderId)
                .FirstOrDefaultAsync(ct);
            if (member is null)
            {
                return null;
            }

            var health = await _provider.GetConnectionHealthAsync(member, ct);
            return health.IsHealthy || health.Issue is null
                ? null
                : ProviderIssueMapping.ToInfo(health.Issue, DateTimeOffset.UtcNow, informational: true); // reads succeeded, so never a gate/give-up input
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCouldNotReadConnectionHealth(_logger, ex, _provider.ProviderName);
            return null;
        }
    }

    /// <summary>Health bookkeeping must never mask the sync outcome: its own failures are logged and dropped.</summary>
    private async Task RecordHealthSafelyAsync(ConnectionIssueInfo? issue, CancellationToken ct)
    {
        if (_health is null)
        {
            return;
        }

        try
        {
            await _health.RecordHealthAsync(_provider.ProviderName, issue, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCouldNotRecordConnectionHealth(_logger, ex, _provider.ProviderName);
        }
    }

    private async Task<SyncResult> SyncCoreAsync(CancellationToken ct)
    {
        _changed.Clear();
        EnsureTenantMayUseProvider();
        var tenantId = TenantId.From(_tenant.TenantId);
        LogStartingSyncForTenantVia(_logger, tenantId, _provider.ProviderName);

        if (_provider.Traits.HasFlag(ConnectorTraits.UserInitiatedLinking)
            && !await _db.ProviderConnections.AnyAsync(c => c.ProviderName == _provider.ProviderName, ct))
        {
            return new SyncResult(0, 0, 0, 0); // a user-initiated connector this tenant has not linked yet: nothing to sync
        }

        var memberRef = await GetOrProvisionConnectionAsync(
            tenantId, ct, allowProvision: _provider.Traits.HasFlag(ConnectorTraits.ProvisionsLocally)); // no billable call (a household-only connector only ever reaches here for the household tenant, see EnsureTenantMayUseProvider)

        // Persist the real member id once the placeholder resolves, so later syncs skip ListMembers.
        if (_provider is IMemberIdResolver resolver)
        {
            var resolved = await resolver.TryResolveMemberProviderIdAsync(memberRef.MemberProviderId, ct);
            if (resolved is not null && resolved != memberRef.MemberProviderId)
            {
                memberRef.ResolveMember(resolved, DateTimeOffset.UtcNow);
                await _db.SaveChangesAsync(ct);
            }
        }

        // Step 2: fetch provider accounts
        var providerAccounts = await _provider.GetAccountsAsync(memberRef.MemberProviderId, ct);
        LogProviderReturnedAccounts(_logger, providerAccounts.Count);

        if (_catalog is not null)
        {
            try
            {
                var seen = DateTimeOffset.UtcNow;
                await _catalog.UpsertAsync(providerAccounts
                    .Select(pa => new DiscoveredProviderAccount(
                        _provider.ProviderName, pa.ProviderId, pa.Name, pa.AccountType, pa.Balance.Amount, pa.CurrencyCode, seen))
                    .ToList(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCouldNotUpdateTheProvider(_logger, ex);
            }
        }

        int accountsUpserted = 0;
        int transactionsAdded = 0;
        int transactionsSkipped = 0;

        // Step 3: upsert domain accounts; build a map so recurring flow seeding can resolve
        // provider account ids → domain Account objects.
        var providerIdToAccount = new Dictionary<string, Account>(StringComparer.Ordinal);
        foreach (var pa in providerAccounts)
        {
            var domainAccount = await UpsertAccountAsync(pa, tenantId, ct);
            if (domainAccount is null)
            {
                // Linked to a different connector (or pinned manual): not this connector's account.
                continue;
            }

            if (!domainAccount.IsActive)
            {
                // Disabled by the user: stop tracking it entirely (no snapshots, no transactions, no flows).
                continue;
            }

            accountsUpserted++;
            providerIdToAccount[pa.ProviderId] = domainAccount;

            // Append-only history: CurrentBalance is overwritten each sync, the snapshot is not.
            await _db.BalanceSnapshots.AddAsync(AccountBalanceSnapshotRecord.Create(
                _tenant.TenantId, domainAccount.AccountId.Value, _provider.ProviderName, pa.ProviderId,
                domainAccount.TypeLockedByUser && Account.IsLiabilityType(domainAccount.AccountType)
                    ? ToOwed(pa.Balance.Amount)
                    : pa.Balance.Amount,
                pa.CurrencyCode, DateTimeOffset.UtcNow), ct);

            // Step 4: sync transactions for this account
            var (added, skipped) = await SyncTransactionsAsync(domainAccount, pa.ProviderId, tenantId, ct);
            transactionsAdded += added;
            transactionsSkipped += skipped;
        }

        // Step 5: seed recurring flows from the simulated provider (idempotent — skips existing).
        var recurringFlowsAdded = await SeedRecurringFlowsAsync(providerIdToAccount, tenantId, ct);

        // Flush whatever the loop left pending (account rows, links, seeded flows). Transactions and the sync cursor are already committed per page (EfSyncCursorStore.SetAsync saves), so this is not the only save.
        await _db.SaveChangesAsync(ct);

        // Only now is the sync a success: stamp the throttle clock (#120). A failure above leaves the previous stamp, so a partly
        // failed sync (cursors advanced, then an error) does not start a fresh minimum-interval window.
        await _cursorStore.MarkSuccessfulAsync(_provider.ProviderName, providerIdToAccount.Keys.ToList(), ct);

        var result = new SyncResult(accountsUpserted, transactionsAdded, transactionsSkipped, recurringFlowsAdded, _changed.Snapshot());
        LogSyncCompleteAccountsTransactionsAdded(_logger, result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped, result.RecurringFlowsAdded);

        return result;
    }
}
