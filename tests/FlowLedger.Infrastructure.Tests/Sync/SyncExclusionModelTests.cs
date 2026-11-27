using CsCheck;
using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.TestSupport;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// INV-09 as a stateful model: random interleavings of "start a sync", "finish it", "fail it" and "let time pass" across three tenants
/// are replayed against both the real <see cref="GuardedSyncService"/> and a small model. After every step the two must agree on
/// (a) at most one sync in flight per tenant, (b) a sync inside the minimum interval never reaches the provider, and (c) the "last sync"
/// (the cursor-commit stand-in) moves only when a sync completes successfully, never on a refusal or a failure.
/// </summary>
[Trait("Invariant", "INV-09")]
public sealed class SyncExclusionModelTests
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);

    private enum Kind { Start, Finish, Fail, Wait }

    private readonly record struct Op(Kind Kind, int Tenant, int Minutes);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The provider call. It blocks until the test releases it, and "commits" (records the last-sync time) only on success.</summary>
    private sealed class GatedProvider(ManualClock clock) : IFinancialSyncService
    {
        private TaskCompletionSource<bool>? _gate;

        public int Calls { get; private set; }

        public int Concurrent { get; private set; }

        public int MaxConcurrent { get; private set; }

        public DateTimeOffset? LastCommit { get; private set; }

        public Task<string> ConnectAsync(CancellationToken ct = default) => Task.FromResult("m");

        public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            Concurrent++;
            MaxConcurrent = Math.Max(MaxConcurrent, Concurrent);
            _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var ok = await _gate.Task;
                if (!ok)
                {
                    throw new InvalidOperationException("provider failed");
                }

                LastCommit = clock.GetUtcNow();
                return SyncResultBuilder.A().Build();
            }
            finally
            {
                Concurrent--;
            }
        }

        public void Release(bool success) => _gate!.SetResult(success);
    }

    private sealed class Model
    {
        public bool Running;
        public DateTimeOffset? LastCommit;
    }

    private static readonly Gen<List<Op>> Script = Gen.Select(Gen.Int[0, 3], Gen.Int[0, 2], Gen.Int[1, 45], (k, t, m) => new Op((Kind)k, t, m)).List[1, 40];

    private static async Task Run(List<Op> ops)
    {
        var clock = new ManualClock(TestClock.Instant);
        var tenants = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var providers = tenants.Select(_ => new GatedProvider(clock)).ToArray();
        var models = tenants.Select(_ => new Model()).ToArray();
        var inflight = new Task<SyncResult>?[3];

        GuardedSyncService Service(int t) => new(providers[t], tenants[t], MinInterval, _ => Task.FromResult(providers[t].LastCommit), clock);

        foreach (var op in ops)
        {
            var t = op.Tenant;
            switch (op.Kind)
            {
                case Kind.Start:
                    {
                        var callsBefore = providers[t].Calls;
                        var task = Service(t).SyncAsync();
                        if (models[t].Running)
                        {
                            (await Assert.ThrowsAsync<SyncAlreadyRunningException>(() => task)).Should().NotBeNull();
                            providers[t].Calls.Should().Be(callsBefore, "a refused second sync must not reach the provider");
                        }
                        else if (models[t].LastCommit is { } last && last + MinInterval > clock.GetUtcNow())
                        {
                            (await Assert.ThrowsAsync<SyncThrottledException>(() => task)).Should().NotBeNull();
                            providers[t].Calls.Should().Be(callsBefore, "a sync inside the minimum interval must not reach the provider");
                        }
                        else
                        {
                            // The guard checks the interval before it calls the provider: wait until the provider call has started.
                            while (providers[t].Calls == callsBefore && !task.IsCompleted)
                            {
                                await Task.Yield();
                            }

                            task.IsCompleted.Should().BeFalse("an allowed sync is waiting on the provider");
                            models[t].Running = true;
                            inflight[t] = task;
                        }

                        break;
                    }

                case Kind.Finish when models[t].Running:
                    providers[t].Release(true);
                    await inflight[t]!;
                    models[t].Running = false;
                    models[t].LastCommit = clock.GetUtcNow();
                    break;

                case Kind.Fail when models[t].Running:
                    providers[t].Release(false);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => inflight[t]!);
                    models[t].Running = false;
                    break;

                case Kind.Wait:
                    clock.Advance(TimeSpan.FromMinutes(op.Minutes));
                    break;
            }

            for (var i = 0; i < 3; i++)
            {
                providers[i].MaxConcurrent.Should().BeLessThanOrEqualTo(1, "at most one sync per tenant at a time");
                providers[i].LastCommit.Should().Be(models[i].LastCommit, "the last-sync (cursor commit) moves only on a successful completion");
                GuardedSyncService.InFlight.Contains(tenants[i]).Should().Be(models[i].Running, "the slot is held exactly while a sync runs");
            }
        }

        // Drain: whatever is still running finishes, and every slot is released.
        for (var i = 0; i < 3; i++)
        {
            if (models[i].Running)
            {
                providers[i].Release(true);
                await inflight[i]!;
            }

            GuardedSyncService.InFlight.Contains(tenants[i]).Should().BeFalse();
        }
    }

    [Fact]
    public void Random_interleavings_agree_with_the_exclusion_model() =>
        Prop.ForAll(Script, ops => Run(ops).GetAwaiter().GetResult(), iterations: 300);
}
