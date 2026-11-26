using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class JobRunStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    // job_runs is created ad hoc (outside Respawn's table list), so drop it for the other tests sharing the DB.
    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS job_runs", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [DockerFact]
    public async Task Runs_are_persisted_newest_first_and_old_rows_are_pruned()
    {
        var store = new NpgsqlJobRunStore(fixture.ConnectionString);
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(new JobRunEntry("ScheduledBackupJob", now.AddDays(-200), now.AddDays(-200).AddSeconds(5), true, null, 1));
        await store.AppendAsync(new JobRunEntry("ScheduledBackupJob", now.AddMinutes(-10), now.AddMinutes(-10).AddSeconds(3), false, "boom", 1));
        await store.AppendAsync(new JobRunEntry("ScheduledBackupJob", now.AddMinutes(-5), now.AddMinutes(-5).AddSeconds(4), true, null, 2));

        var runs = await store.RecentAsync(10);

        runs.Should().HaveCount(2, "the 200 day old row is beyond retention and was deleted by the newer inserts");
        runs[0].Success.Should().BeTrue();
        runs[0].Attempt.Should().Be(2);
        runs[1].Error.Should().Be("boom");
        runs[1].Success.Should().BeFalse();
    }
}
