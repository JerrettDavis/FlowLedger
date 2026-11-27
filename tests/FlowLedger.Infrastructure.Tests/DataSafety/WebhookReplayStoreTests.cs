using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.DataSafety;

/// <summary>#152: replay detection that survives a restart and works across instances (a fresh store object is a restarted process).</summary>
[Collection("Integration")]
public sealed class WebhookReplayStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task A_replay_is_rejected_after_a_restart_and_a_different_provider_or_body_is_not()
    {
        var hash = Guid.NewGuid().ToString("N");

        (await new EfWebhookReplayStore(new FlowLedger.Infrastructure.Persistence.HostDb(fixture.ConnectionString)).TryRecordAsync("MX", hash, Window)).Should().BeTrue();

        // "Restart": a brand-new store object (no memory) still sees the earlier delivery.
        var restarted = new EfWebhookReplayStore(new FlowLedger.Infrastructure.Persistence.HostDb(fixture.ConnectionString));
        (await restarted.TryRecordAsync("MX", hash, Window)).Should().BeFalse();
        (await restarted.TryRecordAsync("Quiltt", hash, Window)).Should().BeTrue();
        (await restarted.TryRecordAsync("MX", Guid.NewGuid().ToString("N"), Window)).Should().BeTrue();
    }

    [DockerFact]
    public async Task Concurrent_deliveries_of_the_same_body_let_exactly_one_through_and_an_expired_entry_is_accepted_again()
    {
        var hash = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => new EfWebhookReplayStore(new FlowLedger.Infrastructure.Persistence.HostDb(fixture.ConnectionString)).TryRecordAsync("MX", hash, Window)));
        results.Count(r => r).Should().Be(1);

        // Age the row past the window: the same body is then processed again (a legitimate re-send much later).
        await using (var conn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var age = new NpgsqlCommand("UPDATE webhook_replays SET seen_utc = now() - interval '1 hour'", conn);
            await age.ExecuteNonQueryAsync();
        }

        (await new EfWebhookReplayStore(new FlowLedger.Infrastructure.Persistence.HostDb(fixture.ConnectionString)).TryRecordAsync("MX", hash, Window)).Should().BeTrue();
    }
}
