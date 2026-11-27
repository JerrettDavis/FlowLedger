using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>The persisted last-fire mark behind once-per-fire-time and missed-fire catch-up (#187).</summary>
[Collection("Integration")]
public sealed class JobFireMarksTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly IntegrationTestFixture _fixture;
    public JobFireMarksTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private EfJobFireMarks Marks() => new(new HostDb(_fixture.ConnectionString));

    [DockerFact]
    public async Task A_fire_time_is_claimed_once_and_the_mark_only_moves_forward()
    {
        var marks = Marks();

        (await marks.TryClaimAsync("ScheduledSyncJob", Noon)).Should().BeTrue("the first claim of a fire time wins");
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon)).Should().BeFalse("the same fire time is never claimed twice");
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon.AddHours(-1))).Should().BeFalse("an older fire time cannot move the mark back");
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon.AddDays(1))).Should().BeTrue("the next fire time is a new claim");
        (await marks.TryClaimAsync("ScheduledBackupJob", Noon)).Should().BeTrue("marks are per job");
        (await marks.LastFireAsync("ScheduledSyncJob")).Should().Be(Noon.AddDays(1));
        (await marks.LastFireAsync("NeverRan")).Should().BeNull();
    }

    [DockerFact]
    public async Task Many_replicas_racing_for_one_fire_time_produce_exactly_one_winner()
    {
        // Each replica has its own store (and so its own connection), as separate worker processes do.
        var replicas = Enumerable.Range(0, 12).Select(_ => Marks()).ToList();

        var results = await Task.WhenAll(replicas.Select(r => Task.Run(() => r.TryClaimAsync("ScheduledSyncJob", Noon))));

        results.Count(won => won).Should().Be(1);
    }

    [DockerFact]
    public async Task Seeding_never_overwrites_an_existing_mark()
    {
        var marks = Marks();

        await marks.SeedAsync("ScheduledSyncJob", Noon);
        await marks.SeedAsync("ScheduledSyncJob", Noon.AddDays(5));

        (await marks.LastFireAsync("ScheduledSyncJob")).Should().Be(Noon);
        (await marks.TryClaimAsync("ScheduledSyncJob", Noon)).Should().BeFalse("a seeded slot counts as already fired");
    }
}
