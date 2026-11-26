using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
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
/// Domain events (PLAN §9): on <see cref="SaveChangesAsync"/> all uncommitted domain events
/// are collected from tracked aggregate roots, then dispatched after the database transaction
/// commits via <see cref="IDomainEventDispatcher"/>.
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
    private readonly IDomainEventDispatcher _eventDispatcher;

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
    internal DbSet<ProviderAccountRecord> ProviderAccounts => Set<ProviderAccountRecord>();
    internal DbSet<AccountConnectorLinkRecord> AccountConnectorLinks => Set<AccountConnectorLinkRecord>();

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>
    /// Production constructor. Injected by DI.
    /// </summary>
    public FlowLedgerDbContext(
        DbContextOptions<FlowLedgerDbContext> options,
        IDomainEventDispatcher eventDispatcher,
        ITenantContext tenantContext)
        : base(options)
    {
        _eventDispatcher = eventDispatcher;
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    }

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
    }

    // ── SaveChanges ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ValidateTenantBoundaries();
        var (events, aggregates) = CollectDomainEvents();

        var result = await base.SaveChangesAsync(cancellationToken);

        // Only a committed save consumes the events; if it threw they stay on the aggregates (#92).
        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        // Dispatch after successful persistence so handlers see committed state.
        if (events.Count > 0)
        {
            await _eventDispatcher.DispatchAsync(events, cancellationToken);
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

    /// <summary>Collects (without clearing) all uncommitted domain events from tracked aggregate roots.</summary>
    private (List<IDomainEvent> Events, List<IAggregateRoot> Aggregates) CollectDomainEvents()
    {
        var aggregates = ChangeTracker.Entries<IAggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        var events = aggregates
            .SelectMany(a => a.DomainEvents)
            .ToList();

        return (events, aggregates);
    }
}
