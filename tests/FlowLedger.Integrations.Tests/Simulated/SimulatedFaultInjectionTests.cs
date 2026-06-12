using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Simulated;

/// <summary>
/// Verifies that the latency, failure, and rate-limit simulation knobs behave as specified
/// in <see cref="SimulatedProviderOptions"/>.
/// </summary>
public sealed class SimulatedFaultInjectionTests
{
    private static readonly TenantId TestTenant =
        TenantId.From(new Guid("00000000-0000-0000-0000-000000000001"));

    // ── Latency ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task LatencyMs_adds_measurable_delay_to_calls()
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { LatencyMs = 50 }));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await provider.BeginConnectionAsync(TestTenant);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(40,
            because: "LatencyMs=50 should add at least ~40 ms of delay");
    }

    [Fact]
    public async Task Zero_latency_returns_quickly()
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { LatencyMs = 0 }));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await provider.BeginConnectionAsync(TestTenant);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            because: "zero-latency provider should return in well under 500 ms");
    }

    // ── Failure rate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FailureRate_1_always_throws_transient_exception()
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { FailureRate = 1.0 }));

        Func<Task> act = () => provider.BeginConnectionAsync(TestTenant);

        await act.Should().ThrowAsync<TransientProviderException>();
    }

    [Fact]
    public async Task FailureRate_0_never_throws()
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { FailureRate = 0.0 }));

        // Run multiple calls — none should throw
        for (var i = 0; i < 20; i++)
        {
            await provider.BeginConnectionAsync(TestTenant);
        }
    }

    // ── Rate limiting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimitAfter_throws_rate_limited_exception_on_exceeded_call()
    {
        const int limit = 3;
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { RateLimitAfter = limit }));

        // First {limit} calls should succeed
        for (var i = 0; i < limit; i++)
        {
            await provider.BeginConnectionAsync(TestTenant);
        }

        // Next call should throw
        Func<Task> act = () => provider.BeginConnectionAsync(TestTenant);
        await act.Should().ThrowAsync<RateLimitedProviderException>();
    }

    [Fact]
    public async Task RateLimited_exception_has_retry_after_in_the_future()
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { RateLimitAfter = 1 }));

        await provider.BeginConnectionAsync(TestTenant); // consume the 1 allowed call

        var ex = await Assert.ThrowsAsync<RateLimitedProviderException>(
            () => provider.BeginConnectionAsync(TestTenant));

        ex.RetryAfter.Should().NotBeNull();
        ex.RetryAfter!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(-1),
            because: "RetryAfter must be in the near future");
    }

    [Fact]
    public async Task Zero_rate_limit_never_rate_limits()
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { RateLimitAfter = 0 }));

        // 20 calls should all succeed
        for (var i = 0; i < 20; i++)
        {
            await provider.BeginConnectionAsync(TestTenant);
        }
    }

    // ── Transient exception properties ───────────────────────────────────────

    [Fact]
    public async Task Transient_exception_has_retry_after_hint()
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { FailureRate = 1.0 }));

        var ex = await Assert.ThrowsAsync<TransientProviderException>(
            () => provider.BeginConnectionAsync(TestTenant));

        ex.RetryAfter.Should().NotBeNull();
        ex.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
