using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace FlowLedger.Infrastructure.Tests.Helpers;

/// <summary>
/// Shared test fixture that starts a PostgreSQL Testcontainer, applies all EF Core
/// migrations, and provides a Respawn <see cref="Respawner"/> for fast between-test resets.
///
/// The fixture is shared across all tests in the "Integration" xUnit collection.
/// Individual tests call <see cref="ResetAsync"/> at the start to reset data.
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowledger_test")
        .WithUsername("test")
        .WithPassword("test_password_local")
        .Build();

    private Respawner _respawner = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply all migrations to the fresh database.
        await using var ctx = CreateDbContext(null);
        await ctx.Database.MigrateAsync();

        // Configure Respawn to reset all tables between tests.
        // Respawn 7.x requires an open DbConnection.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")]
        });
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Persists a real account for the tenant and returns its id. Composite tenant foreign keys (#146) mean transactions, flows,
    /// snapshots, links and rates can only reference an account that exists in the same tenant.
    /// </summary>
    public async Task<FlowLedger.Domain.ValueObjects.AccountId> SeedAccountAsync(TestTenantContext tenant, string name = "Seeded account")
    {
        await using var ctx = CreateDbContext(tenant);
        var account = FlowLedger.Domain.Aggregates.Account.Create(
            FlowLedger.Domain.ValueObjects.TenantId.From(tenant.TenantId),
            name,
            FlowLedger.Domain.Aggregates.AccountType.Checking,
            new FlowLedger.Domain.ValueObjects.Money(0m, FlowLedger.Domain.ValueObjects.Currency.Usd));
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        return account.AccountId;
    }

    /// <summary>Resets all data between tests. Call at the start of each test.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);
    }

    /// <summary>
    /// Builds a context the way production does: the default internal service provider
    /// (caching ON), so every context shares one cached model. <c>null</c> = unscoped/admin.
    /// </summary>
    public FlowLedgerDbContext CreateProductionStyleContext(TestTenantContext? tenantContext)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FlowLedgerDbContext>();
        optionsBuilder.UseNpgsql(ConnectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name);
            // Same as AddInfrastructure: a retrying execution strategy forbids bare user transactions (#143).
            npgsql.EnableRetryOnFailure(maxRetryCount: 5);
        });

        return new FlowLedgerDbContext(optionsBuilder.Options, new NoOpDomainEventDispatcher(), (ITenantContext?)tenantContext ?? UnscopedTenantContext.Instance);
    }

    /// <summary>
    /// Creates a <see cref="FlowLedgerDbContext"/> scoped to the given tenant context.
    /// Pass <c>null</c> for a context with no tenant filter (admin/migration use).
    /// </summary>
    public FlowLedgerDbContext CreateDbContext(TestTenantContext? tenantContext)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FlowLedgerDbContext>();
        optionsBuilder
            .UseNpgsql(ConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name);
            });

        return new FlowLedgerDbContext(
            optionsBuilder.Options,
            new NoOpDomainEventDispatcher(),
            (ITenantContext?)tenantContext ?? UnscopedTenantContext.Instance);
    }
}
