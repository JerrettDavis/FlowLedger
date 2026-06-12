using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations</c>.
/// Provides a DbContext without a live tenant context so that the EF tools can
/// inspect the model and generate migrations without needing a running application.
/// </summary>
public sealed class FlowLedgerDbContextFactory : IDesignTimeDbContextFactory<FlowLedgerDbContext>
{
    public FlowLedgerDbContext CreateDbContext(string[] args)
    {
        // Use an environment variable when available (CI); fall back to localhost default.
        var connectionString = Environment.GetEnvironmentVariable("FLOWLEDGER_DB")
            ?? "Host=localhost;Port=5432;Database=flowledger;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<FlowLedgerDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(FlowLedgerDbContextFactory).Assembly.GetName().Name);
        });

        // tenantContext is null at design time — global query filters are omitted
        // so migrations see the full schema.
        return new FlowLedgerDbContext(
            optionsBuilder.Options,
            new NoOpDomainEventDispatcher(),
            tenantContext: null);
    }
}
