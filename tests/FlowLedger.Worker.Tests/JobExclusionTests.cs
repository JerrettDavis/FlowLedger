using FlowLedger.Worker.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace FlowLedger.Worker.Tests;

public sealed class JobExclusionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private JobExclusion NewWorkerHost() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:flowledger"] = _postgres.GetConnectionString(),
        }).Build(),
        NullLogger<JobExclusion>.Instance);

    [Fact]
    public async Task Two_worker_hosts_on_one_database_never_run_the_same_job_at_once()
    {
        var hostA = NewWorkerHost();
        var hostB = NewWorkerHost();

        (await hostA.TryEnterAsync("ScheduledBackupJob", "fire-a")).Should().BeTrue();
        (await hostB.TryEnterAsync("ScheduledBackupJob", "fire-b")).Should().BeFalse("host A is still running it");

        // A different job is independent.
        (await hostB.TryEnterAsync("ScheduledSyncJob", "fire-b2")).Should().BeTrue();
        await hostB.ExitAsync("fire-b2");

        await hostA.ExitAsync("fire-a");
        (await hostB.TryEnterAsync("ScheduledBackupJob", "fire-b3")).Should().BeTrue("the lock was released when A finished");
        await hostB.ExitAsync("fire-b3");
    }

    [Fact]
    public async Task Without_a_connection_string_the_job_just_runs()
    {
        var exclusion = new JobExclusion(new ConfigurationBuilder().Build(), NullLogger<JobExclusion>.Instance);
        (await exclusion.TryEnterAsync("anything", "f")).Should().BeTrue();
    }
}
