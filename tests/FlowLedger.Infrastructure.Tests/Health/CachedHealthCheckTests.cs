using FlowLedger.Infrastructure.Health;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowLedger.Infrastructure.Tests.Health;

/// <summary>#184 review: anonymous probes must not cause one database round trip each.</summary>
public sealed class CachedHealthCheckTests
{
    private sealed class Counting : IHealthCheck
    {
        public int Calls;
        public HealthCheckResult Result = HealthCheckResult.Healthy();

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_burst_of_probes_runs_the_inner_check_once_per_window_and_shares_state_across_instances()
    {
        var inner = new Counting();
        var time = new ManualTime();
        var state = new HealthCacheStore().For("postgres");
        var ttl = TimeSpan.FromSeconds(5);

        // New decorator instance per "request", as the health check service creates them, same shared state.
        for (var i = 0; i < 20; i++)
        {
            await new CachedHealthCheck(inner, ttl, time, state).CheckHealthAsync(new HealthCheckContext());
        }

        inner.Calls.Should().Be(1);

        time.Now += TimeSpan.FromSeconds(6);
        inner.Result = HealthCheckResult.Unhealthy("down");
        var after = await new CachedHealthCheck(inner, ttl, time, state).CheckHealthAsync(new HealthCheckContext());

        inner.Calls.Should().Be(2, "the cache expires after the window");
        after.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
