using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Scheduling;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>The persisted last-fire lease behind once-per-fire-time and missed-fire catch-up (#187).</summary>
[Collection("Integration")]
public sealed class JobFireMarksTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private readonly IntegrationTestFixture _fixture;
    public JobFireMarksTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private EfJobFireMarks Marks(TimeProvider? clock = null) => new(new HostDb(_fixture.ConnectionString), clock);

    [DockerFact]
    public async Task A_fire_time_is_claimed_once_and_the_mark_only_moves_forward()
    {
        var marks = Marks();

        (await marks.TryClaimAsync("ScheduledSyncJob", Noon, "a", Lease)).Should().BeTrue("the first claim of a fire time wins");
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon, "b", Lease)).Should().BeFalse("the same fire time is never claimed twice");
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon.AddHours(-1), "b", Lease)).Should().BeFalse("an older fire time cannot move the mark back");
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon.AddDays(1), "b", Lease)).Should().BeTrue("the next fire time is a new claim");
        (await marks.TryClaimAsync("ScheduledBackupJob", Noon, "a", Lease)).Should().BeTrue("marks are per job");
        (await marks.GetAsync("ScheduledSyncJob"))!.LastFireUtc.Should().Be(Noon.AddDays(1));
        (await marks.GetAsync("NeverRan")).Should().BeNull();
    }

    [DockerFact]
    public async Task Many_replicas_racing_for_one_fire_time_produce_exactly_one_winner()
    {
        // Each replica has its own store (and so its own connection), as separate worker processes do.
        var replicas = Enumerable.Range(0, 12).Select(i => (Store: Marks(), Owner: $"replica-{i}")).ToList();

        var results = await Task.WhenAll(replicas.Select(r => Task.Run(() => r.Store.TryClaimAsync("ScheduledSyncJob", Noon, r.Owner, Lease))));

        results.Count(won => won).Should().Be(1);
    }

    [DockerFact]
    public async Task A_claim_whose_owner_died_is_taken_over_only_after_its_lease_and_only_once()
    {
        var clock = new MovableClock(Noon);
        var marks = Marks(clock);
        (await marks.TryClaimAsync("ScheduledBackupJob", Noon, "dead-replica", Lease)).Should().BeTrue();
        // ... the owner crashes: it never confirms.

        clock.Now = Noon.AddMinutes(9);
        (await marks.TryClaimAsync("ScheduledBackupJob", Noon, "survivor", Lease)).Should().BeFalse("the lease has not run out; the owner may still be running");

        clock.Now = Noon.AddMinutes(11);
        (await marks.TryClaimAsync("ScheduledBackupJob", Noon, "survivor", Lease)).Should().BeTrue("an unconfirmed claim past its lease is reclaimable");
        (await marks.TryClaimAsync("ScheduledBackupJob", Noon, "third", Lease)).Should().BeFalse("and only one replica gets it");
        (await marks.GetAsync("ScheduledBackupJob"))!.Owner.Should().Be("survivor");

        (await marks.CompleteAsync("ScheduledBackupJob", Noon, "dead-replica")).Should().BeFalse("a stale owner cannot confirm a claim that was taken over");
        (await marks.CompleteAsync("ScheduledBackupJob", Noon, "survivor")).Should().BeTrue();
        clock.Now = Noon.AddHours(5);
        (await marks.TryClaimAsync("ScheduledBackupJob", Noon, "late", Lease)).Should().BeFalse("a confirmed slot is never reclaimed, however old");
        (await marks.GetAsync("ScheduledBackupJob"))!.CompletedUtc.Should().NotBeNull();
    }

    [DockerFact]
    public async Task Seeding_never_overwrites_an_existing_mark_and_counts_as_done()
    {
        var marks = Marks();

        await marks.SeedAsync("ScheduledSyncJob", Noon);
        await marks.SeedAsync("ScheduledSyncJob", Noon.AddDays(5));

        var mark = (await marks.GetAsync("ScheduledSyncJob"))!;
        mark.LastFireUtc.Should().Be(Noon);
        mark.CompletedUtc.Should().NotBeNull("a seeded slot is treated as already done, not as an abandoned claim");
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon, "a", Lease)).Should().BeFalse();
    }
}
