using FlowLedger.Application;
using FlowLedger.Infrastructure;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace FlowLedger.Bdd.Tests.Support;

/// <summary>
/// Shared BDD integration fixture: starts a PostgreSQL Testcontainer, applies all EF Core
/// migrations, provides a Respawn reset between scenarios, and builds real DI-backed service
/// providers that wire the genuine Application + Infrastructure layers (handlers, EF repos,
/// DbContext, providers) against the container.
///
/// Scenarios drive the real handlers directly (no HTTP). Each scenario resolves a tenant-scoped
/// <see cref="BddScope"/> from this fixture, exactly mirroring how the API host composes the
/// same services — only the connection string, tenant context, and provider config are overridden.
/// </summary>
public sealed class BddTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowledger_bdd_test")
        .WithUsername("test")
        .WithPassword("test_password_local")
        .Build();

    private Respawner _respawner = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply migrations with a tenant-free context so query filters are not required.
        await using var ctx = CreateMigrationDbContext();
        await ctx.Database.MigrateAsync();

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

    /// <summary>Resets all data between scenarios. Call as the first async Given step.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);
    }

    /// <summary>
    /// Builds a real DI container (Application + Infrastructure) scoped to <paramref name="tenant"/>,
    /// wired to the Testcontainer. Extra config keys (e.g. enabling the MX provider with a WireMock
    /// base URL) can be supplied via <paramref name="extraConfig"/>.
    ///
    /// Returns a <see cref="BddScope"/> that owns a DI scope; dispose it when the scenario ends.
    /// </summary>
    public BddScope CreateScope(
        TestTenantContext tenant,
        IReadOnlyDictionary<string, string?>? extraConfig = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["ConnectionStrings:flowledger"] = ConnectionString,
            // Default: Simulated provider (no credentials required).
            ["Mx:Enabled"] = "false",
        };

        if (extraConfig is not null)
        {
            foreach (var kvp in extraConfig)
            {
                configValues[kvp.Key] = kvp.Value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        // The host normally registers logging; the BDD container must too because
        // Infrastructure services (e.g. DispatchingDomainEventDispatcher, FinancialSyncService)
        // depend on ILogger<T>.
        services.AddLogging();

        // Real layers — the same registrations the API host uses.
        services.AddApplication();
        services.AddInfrastructure(configuration);

        // Override tenant resolution with the deterministic test tenant. The DbContext consumes
        // ITenantContext to build its per-instance query filters, so this controls isolation.
        services.AddScoped<ITenantContext>(_ => tenant);

        var provider = services.BuildServiceProvider();
        return new BddScope(provider);
    }

    /// <summary>Tenant-free context used only for migrations.</summary>
    private FlowLedgerDbContext CreateMigrationDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<FlowLedgerDbContext>();
        optionsBuilder.UseNpgsql(ConnectionString, npgsql =>
            npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name));

        return new FlowLedgerDbContext(
            optionsBuilder.Options,
            new NoOpDomainEventDispatcher(),
            tenantContext: null);
    }

    private sealed class NoOpDomainEventDispatcher : FlowLedger.Application.Abstractions.IDomainEventDispatcher
    {
        public Task DispatchAsync(
            IReadOnlyList<IDomainEvent> events,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

/// <summary>
/// A disposable DI scope for a single scenario. Resolve handlers/services from <see cref="Services"/>;
/// they share a single scoped <see cref="FlowLedgerDbContext"/> and the scenario's tenant context.
/// </summary>
public sealed class BddScope : IDisposable
{
    private readonly ServiceProvider _root;
    private readonly IServiceScope _scope;

    internal BddScope(ServiceProvider root)
    {
        _root = root;
        _scope = root.CreateScope();
    }

    public IServiceProvider Services => _scope.ServiceProvider;

    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.Dispose();
        _root.Dispose();
    }
}

/// <summary>xUnit collection so the Postgres container is shared across all BDD scenarios.</summary>
[CollectionDefinition(Name)]
public sealed class BddIntegrationCollection : ICollectionFixture<BddTestFixture>
{
    public const string Name = "BddIntegration";
}
