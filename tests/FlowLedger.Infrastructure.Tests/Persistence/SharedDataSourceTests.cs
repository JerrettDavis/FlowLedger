using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>One bounded, named data source serves every context in a process (#180).</summary>
[Collection("Integration")]
public sealed class SharedDataSourceTests : IAsyncLifetime
{
    private const int PoolLimit = 6;
    private readonly IntegrationTestFixture _fixture;
    public SharedDataSourceTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Normalize_applies_the_pool_policy_but_never_overrides_what_the_connection_string_sets()
    {
        var defaults = new NpgsqlConnectionStringBuilder(DatabaseDataSource.Normalize(
            "Host=h;Database=d;Username=u;Password=p", new DatabaseConnectionOptions { ApplicationName = "flowledger-test" }));
        defaults.MaxPoolSize.Should().Be(new DatabaseConnectionOptions().MaxPoolSize);
        defaults.Timeout.Should().Be(15);
        defaults.CommandTimeout.Should().Be(60);
        defaults.ApplicationName.Should().Be("flowledger-test");

        var explicitly = new NpgsqlConnectionStringBuilder(DatabaseDataSource.Normalize(
            "Host=h;Database=d;Username=u;Password=p;Maximum Pool Size=7;Timeout=3;Command Timeout=9;Application Name=mine", new DatabaseConnectionOptions()));
        explicitly.MaxPoolSize.Should().Be(7);
        explicitly.Timeout.Should().Be(3);
        explicitly.CommandTimeout.Should().Be(9);
        explicitly.ApplicationName.Should().Be("mine");
    }

    [DockerFact]
    public async Task Sixteen_tenants_by_fifty_concurrent_requests_stay_inside_the_pool_and_never_see_each_others_rows()
    {
        var appName = "fl-load-" + Guid.NewGuid().ToString("N")[..8];
        await using var dataSource = DatabaseDataSource.Create(
            _fixture.ConnectionString, new DatabaseConnectionOptions { MaxPoolSize = PoolLimit, ApplicationName = appName });
        var tenants = Enumerable.Range(0, 16).Select(_ => TestTenantContext.New()).ToList();
        for (var i = 0; i < tenants.Count; i++)
        {
            for (var k = 0; k <= i; k++)
            {
                await _fixture.SeedAccountAsync(tenants[i], $"acct {k}");
            }
        }

        // Sample the server's own view of how many sessions this application holds, from a connection outside the pool under test.
        var monitorCs = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Pooling = false, ApplicationName = "fl-monitor" }.ConnectionString;
        var peak = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            await using var monitor = new NpgsqlConnection(monitorCs);
            await monitor.OpenAsync();
            while (!stop.IsCancellationRequested)
            {
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name = @a", monitor);
                cmd.Parameters.AddWithValue("a", appName);
                peak = Math.Max(peak, Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(2);
            }
        });

        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(dataSource, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .Options;
        var requests = tenants.SelectMany((tenant, index) => Enumerable.Range(0, 50).Select(async _ =>
        {
            await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);
            var count = await db.Accounts.CountAsync();
            await Task.Delay(1); // hold the connection a moment so requests really overlap
            return (index, count);
        })).ToList();

        var results = await Task.WhenAll(requests);
        await stop.CancelAsync();
        await sampler;

        results.Should().HaveCount(800);
        results.Should().OnlyContain(r => r.count == r.index + 1, "each request sees only its own tenant's accounts");
        peak.Should().BeGreaterThan(0, "the sampler saw the named sessions");
        peak.Should().BeLessThanOrEqualTo(PoolLimit, "800 overlapping requests queue on the pool instead of opening a connection each");
    }
}
