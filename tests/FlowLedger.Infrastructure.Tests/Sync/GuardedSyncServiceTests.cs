using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Sync;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Sync;

[Trait("Invariant", "INV-09")]
public sealed class GuardedSyncServiceTests
{
    private sealed class FakeSync : IFinancialSyncService
    {
        public int Calls { get; private set; }

        public TaskCompletionSource? Gate { get; set; }

        public Task<string> ConnectAsync(CancellationToken ct = default) => Task.FromResult("m");

        public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return new SyncResult(1, 2, 3, 4);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Syncs_inside_min_interval_are_refused_without_calling_inner()
    {
        var inner = new FakeSync();
        var svc = new GuardedSyncService(inner, Guid.NewGuid(), TimeSpan.FromMinutes(30),
            _ => Task.FromResult<DateTimeOffset?>(Now.AddMinutes(-10)), new Clock(Now));

        var act = () => svc.SyncAsync();

        var ex = (await act.Should().ThrowAsync<SyncThrottledException>()).Which;
        ex.RetryAfter.Should().Be(TimeSpan.FromMinutes(20));
        inner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Sync_after_interval_or_first_sync_runs()
    {
        var inner = new FakeSync();
        var old = new GuardedSyncService(inner, Guid.NewGuid(), TimeSpan.FromMinutes(30),
            _ => Task.FromResult<DateTimeOffset?>(Now.AddMinutes(-31)), new Clock(Now));
        var first = new GuardedSyncService(inner, Guid.NewGuid(), TimeSpan.FromMinutes(30),
            _ => Task.FromResult<DateTimeOffset?>(null), new Clock(Now));

        await old.SyncAsync();
        await first.SyncAsync();

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Zero_interval_never_throttles()
    {
        var inner = new FakeSync();
        var svc = new GuardedSyncService(inner, Guid.NewGuid(), TimeSpan.Zero,
            _ => Task.FromResult<DateTimeOffset?>(Now), new Clock(Now));

        await svc.SyncAsync();
        await svc.SyncAsync();

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_sync_for_same_tenant_is_refused()
    {
        var tenant = Guid.NewGuid();
        var inner = new FakeSync { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var svc = new GuardedSyncService(inner, tenant, TimeSpan.Zero, _ => Task.FromResult<DateTimeOffset?>(null), new Clock(Now));

        var first = svc.SyncAsync();
        var act = () => svc.SyncAsync();
        await act.Should().ThrowAsync<SyncAlreadyRunningException>();

        inner.Gate.SetResult();
        await first;
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task In_flight_tenant_state_is_released_after_each_sync_so_it_cannot_grow_with_tenants_seen()
    {
        var before = GuardedSyncService.InFlight.Count;
        var inner = new FakeSync();
        for (var i = 0; i < 2000; i++)
        {
            await new GuardedSyncService(inner, Guid.NewGuid(), TimeSpan.Zero, _ => Task.FromResult<DateTimeOffset?>(null), new Clock(Now)).SyncAsync();
        }

        GuardedSyncService.InFlight.Count.Should().Be(before, "entries exist only while a sync is running (#179)");
    }

    [Fact]
    public async Task A_failed_or_throttled_sync_still_releases_its_slot()
    {
        var tenant = Guid.NewGuid();
        var throttled = new GuardedSyncService(new FakeSync(), tenant, TimeSpan.FromMinutes(30),
            _ => Task.FromResult<DateTimeOffset?>(Now.AddMinutes(-1)), new Clock(Now));
        await FluentActions.Awaiting(() => throttled.SyncAsync()).Should().ThrowAsync<SyncThrottledException>();

        var inner = new FakeSync();
        await new GuardedSyncService(inner, tenant, TimeSpan.Zero, _ => Task.FromResult<DateTimeOffset?>(null), new Clock(Now)).SyncAsync();
        inner.Calls.Should().Be(1);
    }
}
