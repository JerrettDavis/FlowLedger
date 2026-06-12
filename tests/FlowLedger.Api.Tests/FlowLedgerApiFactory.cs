using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace FlowLedger.Api.Tests;

/// <summary>
/// Custom WebApplicationFactory for FlowLedger API integration tests.
/// Starts a PostgreSQL Testcontainer, supplies the connection string + a test API key
/// via in-memory configuration, applies migrations (via a standalone tenant-free context
/// so it works in both Development and Production), and provides Respawn-based reset.
///
/// Defaults to the Development environment so DevTenantContext is used.
/// <see cref="FailClosedApiFactory"/> overrides the environment to Production to test
/// fail-closed tenant resolution.
/// </summary>
public class FlowLedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowledger_api_test")
        .WithUsername("test")
        .WithPassword("test_password_local")
        .Build();

    private Respawner _respawner = null!;

    /// <summary>The API key used for authenticated test requests. Non-default so the
    /// Production startup guard is satisfied in <see cref="FailClosedApiFactory"/>.</summary>
    public const string DevApiKey = "integration-test-key-not-for-production";

    /// <summary>The demo tenant ID used when tests need a valid tenant header.</summary>
    public static readonly Guid DemoTenantId = new("00000000-0000-0000-0000-000000000001");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Apply migrations using a standalone context with a NULL tenant context, so it
        // works regardless of environment (HeaderTenantContext would fail closed with no
        // HttpContext) and without starting the host's hosted services.
        await using var ctx = CreateMigrationDbContext();
        await ctx.Database.MigrateAsync();

        // Initialize Respawn for between-test resets.
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")]
        });
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>Resets the database to a clean state. Call at the start of each test.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);
    }

    /// <summary>Creates an HttpClient pre-configured with the test API key and demo tenant header.</summary>
    public HttpClient CreateAuthenticatedClient(Guid? tenantId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {DevApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", (tenantId ?? DemoTenantId).ToString());
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Supply the connection string early so AddInfrastructure's
            // GetConnectionString("flowledger") resolves to the test container,
            // plus the test API key and a disabled MX provider.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:flowledger"] = _postgres.GetConnectionString(),
                ["Api:Key"] = DevApiKey,
                ["Mx:Enabled"] = "false",
            });
        });
    }

    private FlowLedgerDbContext CreateMigrationDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<FlowLedgerDbContext>();
        optionsBuilder.UseNpgsql(_postgres.GetConnectionString(), npgsql =>
            npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name));

        // Null tenant context → no query filters / no tenant resolution required.
        return new FlowLedgerDbContext(optionsBuilder.Options, new NoOpDomainEventDispatcher(), tenantContext: null);
    }

    private sealed class NoOpDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
