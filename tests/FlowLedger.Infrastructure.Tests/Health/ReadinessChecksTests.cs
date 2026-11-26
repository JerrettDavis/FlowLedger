using FlowLedger.Infrastructure.Health;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowLedger.Infrastructure.Tests.Health;

public sealed class ReadinessChecksTests
{
    [Theory]
    [InlineData("redis:6379", "redis", 6379)]
    [InlineData("localhost:6380,password=x,ssl=true", "localhost", 6380)]
    [InlineData("cache", "cache", 6379)]
    [InlineData("redis://user:pw@host.local:7000", "host.local", 7000)]
    [InlineData("redis://host.local", "host.local", 6379)]
    public void Parses_redis_endpoints(string cs, string host, int port)
    {
        RedisReadinessCheck.TryParseEndpoint(cs, out var h, out var p).Should().BeTrue();
        h.Should().Be(host);
        p.Should().Be(port);
    }

    [Fact]
    public async Task Redis_check_is_healthy_when_not_configured_and_unhealthy_when_unreachable()
    {
        var none = new RedisReadinessCheck(new ConfigurationBuilder().Build());
        (await none.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        var down = new RedisReadinessCheck(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:redis"] = "127.0.0.1:1" }).Build());
        (await down.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Postgres_check_is_unhealthy_without_connection_string_or_server()
    {
        var missing = new PostgresReadinessCheck(new ConfigurationBuilder().Build());
        (await missing.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);

        var down = new PostgresReadinessCheck(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:flowledger"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1",
        }).Build());
        (await down.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Backup_check_is_healthy_when_disabled_and_degraded_when_database_unavailable()
    {
        var off = new BackupFreshnessCheck(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Enabled"] = "false" }).Build(),
            TimeProvider.System);
        (await off.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        var down = new BackupFreshnessCheck(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:flowledger"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1",
        }).Build(), TimeProvider.System);
        (await down.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
    }
    [Fact]
    public async Task ObjectStorage_check_is_healthy_when_writable_and_unhealthy_when_root_is_a_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fl-os-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ok = new ObjectStorageWritableCheck(Microsoft.Extensions.Options.Options.Create(new FlowLedger.Infrastructure.Storage.ObjectStorageOptions { RootPath = dir }));
            (await ok.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
            Directory.GetFiles(dir).Should().BeEmpty("the probe file is removed");

            var file = Path.Combine(dir, "not-a-dir");
            await File.WriteAllTextAsync(file, "x");
            var bad = new ObjectStorageWritableCheck(Microsoft.Extensions.Options.Options.Create(new FlowLedger.Infrastructure.Storage.ObjectStorageOptions { RootPath = file }));
            (await bad.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task LastSync_check_is_degraded_never_unhealthy_when_database_unavailable()
    {
        var down = new LastSyncAgeCheck(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:flowledger"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1",
        }).Build(), TimeProvider.System);
        (await down.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
    }
}
