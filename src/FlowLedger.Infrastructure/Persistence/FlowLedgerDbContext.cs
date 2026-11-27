using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Events;
using FlowLedger.Infrastructure.Persistence.Configurations;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Primary EF Core DbContext for FlowLedger.
///
/// Multi-tenancy (PLAN §13): every aggregate root receives a global query filter keyed off
/// <see cref="ITenantContext.TenantId"/>. All queries are automatically scoped to the
/// current tenant. A tenant context is mandatory; admin, design-time and restore-drill code
/// passes <see cref="UnscopedTenantContext.Instance"/> explicitly to opt out of the filter.
///
/// Domain events (PLAN §9, #92): on <see cref="SaveChangesAsync"/> all uncommitted domain events
/// are collected from tracked aggregate roots and written to the transactional outbox in the SAME
/// transaction as the aggregate change. A dispatcher delivers them to handlers afterwards, with retry
/// (see <c>OutboxProcessor</c>); nothing is dispatched inline.
/// </summary>
public sealed class FlowLedgerDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    /// <summary>True for the explicit admin/design-time scope (<see cref="UnscopedTenantContext"/>).</summary>
    internal bool IsUnscoped => _tenantContext is UnscopedTenantContext;

    /// <summary>The tenant this context is scoped to; null only for an explicitly unscoped context.</summary>
    internal Guid? CurrentTenantId => IsUnscoped ? null : _tenantContext.TenantId;

    // Read by the query filters at query time (EF parameterises context members), so the
    // cached model is identical for every context and never depends on who built it first.
    private Guid FilterTenantGuid => IsUnscoped ? Guid.Empty : _tenantContext.TenantId;

    // ── DbSets ────────────────────────────────────────────────────────────────

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<RecurringFlow> RecurringFlows => Set<RecurringFlow>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Asset> Assets => Set<Asset>();
    internal DbSet<SyncCursorRecord> SyncCursors => Set<SyncCursorRecord>();
    internal DbSet<ProviderConnectionRecord> ProviderConnections => Set<ProviderConnectionRecord>();
    internal DbSet<AccountBalanceSnapshotRecord> BalanceSnapshots => Set<AccountBalanceSnapshotRecord>();
    internal DbSet<SavedReportRecord> SavedReports => Set<SavedReportRecord>();
    internal DbSet<SheetBillRecord> SheetBills => Set<SheetBillRecord>();
    internal DbSet<SheetPaymentRecord> SheetPayments => Set<SheetPaymentRecord>();
    internal DbSet<RecurringDismissalRecord> RecurringDismissals => Set<RecurringDismissalRecord>();
    internal DbSet<SuspiciousDismissalRecord> SuspiciousDismissals => Set<SuspiciousDismissalRecord>();
    internal DbSet<AccountRateRecord> AccountRates => Set<AccountRateRecord>();
    internal DbSet<BudgetRecord> Budgets => Set<BudgetRecord>();
    internal DbSet<GoalRecord> Goals => Set<GoalRecord>();
    internal DbSet<CategoryRuleRecord> CategoryRules => Set<CategoryRuleRecord>();
    internal DbSet<MerchantAliasRecord> MerchantAliases => Set<MerchantAliasRecord>();
    internal DbSet<DuplicateDismissalRecord> DuplicateDismissals => Set<DuplicateDismissalRecord>();
    internal DbSet<MergedTransactionRecord> MergedTransactions => Set<MergedTransactionRecord>();
    internal DbSet<IntegrationDeliveryRecord> IntegrationDeliveries => Set<IntegrationDeliveryRecord>();
    internal DbSet<IntegrationRecord> Integrations => Set<IntegrationRecord>();
    internal DbSet<HomeAssistantSentRecord> HomeAssistantSent => Set<HomeAssistantSentRecord>();
    internal DbSet<HomeAssistantSettingsRecord> HomeAssistantSettings => Set<HomeAssistantSettingsRecord>();
    internal DbSet<ProviderAccountRecord> ProviderAccounts => Set<ProviderAccountRecord>();
    internal DbSet<AccountConnectorLinkRecord> AccountConnectorLinks => Set<AccountConnectorLinkRecord>();
    internal DbSet<OutboxMessageRecord> OutboxMessages => Set<OutboxMessageRecord>();

    // Host-wide tables (#154): no tenant column and no query filter; reached only through HostDb.
    internal DbSet<ProviderApiCallRecord> ProviderApiCalls => Set<ProviderApiCallRecord>();
    internal DbSet<BackupRunRecord> BackupRuns => Set<BackupRunRecord>();
    internal DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();
    internal DbSet<JobRunRecord> JobRuns => Set<JobRunRecord>();
    internal DbSet<JobFireMarkRecord> JobFireMarks => Set<JobFireMarkRecord>();
    internal DbSet<WebhookReplayRecord> WebhookReplays => Set<WebhookReplayRecord>();
    internal DbSet<TenantRegistryRecord> Tenants => Set<TenantRegistryRecord>();
    internal DbSet<AppUserRecord> AppUsers => Set<AppUserRecord>();
    internal DbSet<AppUserLinkCodeRecord> AppUserLinkCodes => Set<AppUserLinkCodeRecord>();
    internal DbSet<SystemSettingRecord> SystemSettings => Set<SystemSettingRecord>();

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>
    /// Production constructor. Injected by DI.
    /// </summary>
    public FlowLedgerDbContext(
        DbContextOptions<FlowLedgerDbContext> options,
        IDomainEventDispatcher eventDispatcher, // retained for constructor compatibility; delivery now goes through the outbox
        ITenantContext tenantContext,
        FlowLedger.Application.Abstractions.ITenantDataVersion? dataVersion = null)
        : base(options)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _dataVersion = dataVersion;
    }

    private readonly FlowLedger.Application.Abstractions.ITenantDataVersion? _dataVersion;

    /// <summary>True when the pending changes touch data the cached dashboard/insight results are derived from.</summary>
    private bool TouchesCachedReads() => ChangeTracker.Entries().Any(e =>
        e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
        && e.Entity is Transaction or Account or Category or AccountBalanceSnapshotRecord);

    // ── Model building ───────────────────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply one IEntityTypeConfiguration<T> per aggregate.
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new TransactionConfiguration());
        modelBuilder.ApplyConfiguration(new RecurringFlowConfiguration());
        modelBuilder.ApplyConfiguration(new CategoryConfiguration());
        modelBuilder.ApplyConfiguration(new AssetConfiguration());
        modelBuilder.ApplyConfiguration(new SyncCursorRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ProviderConnectionRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AccountBalanceSnapshotRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SavedReportRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SheetBillRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SheetPaymentRecordConfiguration());
        modelBuilder.ApplyConfiguration(new RecurringDismissalRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SuspiciousDismissalRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AccountRateRecordConfiguration());
        modelBuilder.ApplyConfiguration(new BudgetRecordConfiguration());
        modelBuilder.ApplyConfiguration(new GoalRecordConfiguration());
        modelBuilder.ApplyConfiguration(new CategoryRuleRecordConfiguration());
        modelBuilder.ApplyConfiguration(new MerchantAliasRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AccountConnectorLinkRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ProviderAccountRecordConfiguration());
        modelBuilder.ApplyConfiguration(new HomeAssistantSettingsRecordConfiguration());
        modelBuilder.ApplyConfiguration(new HomeAssistantSentRecordConfiguration());
        modelBuilder.ApplyConfiguration(new IntegrationRecordConfiguration());
        modelBuilder.ApplyConfiguration(new IntegrationDeliveryRecordConfiguration());
        modelBuilder.ApplyConfiguration(new MergedTransactionRecordConfiguration());
        modelBuilder.ApplyConfiguration(new DuplicateDismissalRecordConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ProviderApiCallRecordConfiguration());
        modelBuilder.ApplyConfiguration(new BackupRunRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEventRecordConfiguration());
        modelBuilder.ApplyConfiguration(new JobRunRecordConfiguration());
        modelBuilder.ApplyConfiguration(new JobFireMarkRecordConfiguration());
        modelBuilder.ApplyConfiguration(new WebhookReplayRecordConfiguration());
        modelBuilder.ApplyConfiguration(new TenantRegistryRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AppUserRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AppUserLinkCodeRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SystemSettingRecordConfiguration());

        // ── Global query filters (PLAN §13) ──────────────────────────────────
        // Always built (never conditional on the tenant context). The tenant is read from the
        // context instance at query time, and an explicitly unscoped context bypasses the filter.
        modelBuilder.Entity<Account>()
            .HasQueryFilter(a => IsUnscoped || EF.Property<TenantId>(a, "TenantId") == TenantId.From(FilterTenantGuid));
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter(t => IsUnscoped || EF.Property<TenantId>(t, "TenantId") == TenantId.From(FilterTenantGuid));
        modelBuilder.Entity<RecurringFlow>()
            .HasQueryFilter(rf => IsUnscoped || EF.Property<TenantId>(rf, "TenantId") == TenantId.From(FilterTenantGuid));
        modelBuilder.Entity<Category>()
            .HasQueryFilter(c => IsUnscoped || EF.Property<TenantId>(c, "TenantId") == TenantId.From(FilterTenantGuid));
        modelBuilder.Entity<Asset>()
            .HasQueryFilter(a => IsUnscoped || EF.Property<TenantId>(a, "TenantId") == TenantId.From(FilterTenantGuid));
        modelBuilder.Entity<SyncCursorRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<ProviderConnectionRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<AccountBalanceSnapshotRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<SavedReportRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<SheetBillRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<SheetPaymentRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<RecurringDismissalRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<SuspiciousDismissalRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<AccountRateRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<BudgetRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<GoalRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<CategoryRuleRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<MerchantAliasRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<AccountConnectorLinkRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<ProviderAccountRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<HomeAssistantSettingsRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<HomeAssistantSentRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<IntegrationRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<IntegrationDeliveryRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<MergedTransactionRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<DuplicateDismissalRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
        modelBuilder.Entity<OutboxMessageRecord>()
            .HasQueryFilter(r => IsUnscoped || r.TenantId == FilterTenantGuid);
    }

    // ── SaveChanges ──────────────────────────────────────────────────────────

    /// <summary>Attempts a save makes after a concurrency conflict when a <see cref="ConcurrencyConflictResolver"/> is installed.</summary>
    internal const int MaxConflictResolutions = 3;

    /// <summary>
    /// Background pipelines that cannot be re-run (a sync has already paid for the provider calls) install a resolver for their
    /// duration: it reconciles a lost optimistic-concurrency race (returns true to retry the save, false to rethrow). Without one,
    /// a conflict throws <see cref="DbUpdateConcurrencyException"/>, which the API maps to 409 (#157).
    /// </summary>
    internal Func<DbUpdateConcurrencyException, CancellationToken, Task<bool>>? ConcurrencyConflictResolver { get; set; }

    /// <inheritdoc/>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SaveOnceAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
                when (ConcurrencyConflictResolver is { } resolve && attempt <= MaxConflictResolutions)
            {
                if (!await resolve(ex, cancellationToken))
                {
                    throw;
                }
            }
        }
    }

    private async Task<int> SaveOnceAsync(CancellationToken cancellationToken)
    {
        ValidateTenantBoundaries();
        var (outbox, aggregates) = CollectDomainEvents();
        OutboxMessages.AddRange(outbox);

        var touchesCachedReads = _dataVersion is not null && TouchesCachedReads();
        int result;
        try
        {
            // Aggregate changes and their outbox rows commit (or roll back) together.
            result = await base.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A failed save keeps the events on the aggregates (#92), so a retry re-collects them; drop the unsaved rows so they are not duplicated.
            foreach (var row in outbox)
            {
                Entry(row).State = EntityState.Detached;
            }

            throw;
        }

        if (touchesCachedReads)
        {
            _dataVersion!.Bump(_tenantContext.TenantId);   // explicit invalidation of the tenant's cached reads (#160)
        }

        // Only a committed save consumes the events.
        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        return result;
    }

    /// <summary>
    /// Synchronous saves are not supported: domain events are dispatched asynchronously after commit, and blocking on that
    /// (sync-over-async) can deadlock or swallow handler failures (#116, #92). Use <see cref="SaveChangesAsync"/>.
    /// </summary>
    public override int SaveChanges()
        => throw new NotSupportedException("Use SaveChangesAsync: synchronous SaveChanges would block on asynchronous domain event dispatch.");

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that every tracked write stays inside the current tenant. Model-driven (#172): it checks every entity type that
    /// has a TenantId property (aggregates and persistence records alike), so a newly added tenant-owned type is covered
    /// without remembering to list it here.
    /// </summary>
    private void ValidateTenantBoundaries()
    {
        if (IsUnscoped)
        {
            return;
        }

        var current = _tenantContext.TenantId;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Metadata.FindProperty("TenantId") is null)
            {
                continue;
            }

            var owner = entry.Property("TenantId").CurrentValue switch
            {
                TenantId strong => strong.Value,
                Guid raw => raw,
                _ => (Guid?)null,
            };

            if (owner is { } ownerId && ownerId != current)
            {
                throw new InvalidOperationException(
                    $"Cross-tenant mutation attempt: current tenant {current} tried to modify " +
                    $"entity of type {entry.Metadata.ClrType.Name} owned by tenant {ownerId}.");
            }
        }
    }

    /// <summary>Collects (without clearing) the uncommitted domain events of tracked aggregate roots as outbox rows.</summary>
    private (List<OutboxMessageRecord> Rows, List<IAggregateRoot> Aggregates) CollectDomainEvents()
    {
        var rows = new List<OutboxMessageRecord>();
        var aggregates = new List<IAggregateRoot>();

        foreach (var entry in ChangeTracker.Entries<IAggregateRoot>().Where(e => e.Entity.DomainEvents.Count > 0).ToList())
        {
            aggregates.Add(entry.Entity);
            var tenant = entry.Metadata.FindProperty("TenantId") is not null
                ? entry.Property("TenantId").CurrentValue switch
                {
                    TenantId strong => strong.Value,
                    Guid raw => raw,
                    _ => CurrentTenantId ?? Guid.Empty,
                }
                : CurrentTenantId ?? Guid.Empty;

            foreach (var domainEvent in entry.Entity.DomainEvents)
            {
                rows.Add(OutboxMessageRecord.Create(
                    domainEvent.EventId, tenant, OutboxEventSerializer.TypeName(domainEvent),
                    OutboxEventSerializer.Serialize(domainEvent), domainEvent.OccurredAt));
            }
        }

        return (rows, aggregates);
    }
}
