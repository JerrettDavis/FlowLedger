using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Health;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class ReadinessDatabaseTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:flowledger"] = fixture.ConnectionString })
        .Build();

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [DockerFact]
    public async Task Postgres_check_is_healthy_when_migrated_and_unhealthy_with_a_pending_migration()
    {
        var check = new PostgresReadinessCheck(Config());
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        // Forget the newest applied migration: the schema is now "behind" the code.
        await ExecAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\")");
        var behind = await check.CheckHealthAsync(new HealthCheckContext());
        behind.Status.Should().Be(HealthStatus.Unhealthy);
        behind.Description.Should().Contain("pending migration");
    }

    [DockerFact]
    public async Task Backup_check_degrades_when_none_or_stale_and_recovers_after_a_fresh_run()
    {
        var check = new BackupFreshnessCheck(Config(), TimeProvider.System);
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded, "no backup has ever run");

        var store = new BackupRunStore(fixture.ConnectionString);
        var old = DateTimeOffset.UtcNow.AddDays(-3);
        await store.AddAsync(new BackupRunResult(true, old, old, "x", 1, null, null, true, null));
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded, "last success is 3 days old");

        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(new BackupRunResult(true, now, now, "y", 1, null, null, true, null));
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
    }
}
