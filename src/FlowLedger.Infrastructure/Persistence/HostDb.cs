using System.Collections.Concurrent;
using FlowLedger.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Creates explicitly unscoped contexts for the host-wide tables that have no tenant (#154): the provider call ledger, backup runs,
/// job runs, audit log, webhook replay hashes, the tenant registry and the user directory. Those entities carry no tenant filter and
/// are listed as global in the tenant guard tests; everything tenant-owned goes through the scoped <see cref="FlowLedgerDbContext"/>.
/// Safe to use from singletons: each call returns a short-lived context, and the options (so EF's service provider) are shared per
/// connection string.
/// </summary>
internal sealed class HostDb(string connectionString)
{
    private static readonly ConcurrentDictionary<string, DbContextOptions<FlowLedgerDbContext>> OptionsByConnection = new(StringComparer.Ordinal);

    private readonly DbContextOptions<FlowLedgerDbContext> _options = OptionsByConnection.GetOrAdd(
        connectionString,
        cs => new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(cs, npgsql => npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .Options);

    public FlowLedgerDbContext Create() => new(_options, new NoOpDomainEventDispatcher(), UnscopedTenantContext.Instance);
}
