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
    }

    // ── SaveChanges ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenantId();
        var events = CollectDomainEvents();

        var result = await base.SaveChangesAsync(cancellationToken);

        // Dispatch after successful persistence so handlers see committed state.
        if (events.Count > 0)
        {
            await _eventDispatcher.DispatchAsync(events, cancellationToken);
        }

        return result;
    }

    /// <inheritdoc/>
    public override int SaveChanges()
    {
        StampTenantId();
        var events = CollectDomainEvents();

        var result = base.SaveChanges();

        // Fire-and-forget dispatch on sync path (sync callers should prefer async).
        if (events.Count > 0)
        {
            _eventDispatcher.DispatchAsync(events).GetAwaiter().GetResult();
        }

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that all mutations stay within the current tenant boundary.
    /// TenantId is always set by the domain aggregate factories — this method guards
    /// against accidental cross-tenant writes (e.g., a bug passing the wrong aggregate).
    /// </summary>
    private void StampTenantId()
    {
        if (IsUnscoped)
        {
            return;
        }

        var tid = _tenantContext.TenantId; // Guid from ITenantContext

        ValidateTenantBoundary(ChangeTracker.Entries<Account>(), tid);
        ValidateTenantBoundary(ChangeTracker.Entries<Transaction>(), tid);
        ValidateTenantBoundary(ChangeTracker.Entries<RecurringFlow>(), tid);
        ValidateTenantBoundary(ChangeTracker.Entries<Category>(), tid);
        ValidateTenantBoundary(ChangeTracker.Entries<Asset>(), tid);
    }

    private static void ValidateTenantBoundary<T>(
        IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T>> entries,
        Guid currentTenantId)
        where T : class
    {
        foreach (var entry in entries)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var tenantProp = entry.Property("TenantId");
            if (tenantProp.CurrentValue is TenantId storedId && storedId.Value != currentTenantId)
            {
                throw new InvalidOperationException(
                    $"Cross-tenant mutation attempt: current tenant {currentTenantId} tried to modify " +
                    $"entity of type {typeof(T).Name} owned by tenant {storedId.Value}.");
            }
        }
    }

    /// <summary>Collects all uncommitted domain events from tracked aggregate roots and clears them.</summary>
    private List<IDomainEvent> CollectDomainEvents()
    {
        var aggregates = ChangeTracker.Entries<IAggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        var events = aggregates
            .SelectMany(a => a.DomainEvents)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        return events;
    }
}
