using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Configurations;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Primary EF Core DbContext for FlowLedger.
///
/// Multi-tenancy (PLAN §13): every aggregate root receives a global query filter keyed off
/// <see cref="ITenantContext.TenantId"/>. All queries are automatically scoped to the
/// current tenant. Tests may inject a <c>null</c> tenant context to disable the filter.
///
/// Domain events (PLAN §9): on <see cref="SaveChangesAsync"/> all uncommitted domain events
/// are collected from tracked aggregate roots, then dispatched after the database transaction
/// commits via <see cref="IDomainEventDispatcher"/>.
/// </summary>
public sealed class FlowLedgerDbContext : DbContext
{
    private readonly ITenantContext? _tenantContext;
    private readonly IDomainEventDispatcher _eventDispatcher;

    // ── DbSets ────────────────────────────────────────────────────────────────

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<RecurringFlow> RecurringFlows => Set<RecurringFlow>();
    public DbSet<Category> Categories => Set<Category>();

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>
    /// Production constructor. Injected by DI.
    /// </summary>
    public FlowLedgerDbContext(
        DbContextOptions<FlowLedgerDbContext> options,
        IDomainEventDispatcher eventDispatcher,
        ITenantContext? tenantContext = null)
        : base(options)
    {
        _eventDispatcher = eventDispatcher;
        _tenantContext = tenantContext;
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

        // ── Global query filters (PLAN §13) ──────────────────────────────────
        // If _tenantContext is null (design-time or test override), filters are
        // omitted so migrations and cross-tenant admin queries can work.
        if (_tenantContext is not null)
        {
            // The filter expression is stored as an expression tree. EF Core evaluates
            // it against the current context instance on each query. Closing over
            // _tenantContext (an instance field) ensures the correct tenant ID is used
            // per context — provided service provider caching is disabled so each
            // context builds its own model (see AddInfrastructure / test fixture).
            modelBuilder.Entity<Account>()
                .HasQueryFilter(a => EF.Property<TenantId>(a, "TenantId") == TenantId.From(_tenantContext!.TenantId));

            modelBuilder.Entity<Transaction>()
                .HasQueryFilter(t => EF.Property<TenantId>(t, "TenantId") == TenantId.From(_tenantContext!.TenantId));

            modelBuilder.Entity<RecurringFlow>()
                .HasQueryFilter(rf => EF.Property<TenantId>(rf, "TenantId") == TenantId.From(_tenantContext!.TenantId));

            modelBuilder.Entity<Category>()
                .HasQueryFilter(c => EF.Property<TenantId>(c, "TenantId") == TenantId.From(_tenantContext!.TenantId));
        }
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
        if (_tenantContext is null)
        {
            return;
        }

        var tid = _tenantContext.TenantId; // Guid from ITenantContext

        ValidateTenantBoundary(ChangeTracker.Entries<Account>(), tid);
        ValidateTenantBoundary(ChangeTracker.Entries<Transaction>(), tid);
        ValidateTenantBoundary(ChangeTracker.Entries<RecurringFlow>(), tid);
        ValidateTenantBoundary(ChangeTracker.Entries<Category>(), tid);
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
