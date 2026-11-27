using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class JobRunStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Runs_are_persisted_newest_first_and_old_rows_are_pruned()
    {
        var store = new EfJobRunStore(new FlowLedger.Infrastructure.Persistence.HostDb(fixture.ConnectionString));
        var now = FlowLedger.TestSupport.TestClock.Instant;

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
