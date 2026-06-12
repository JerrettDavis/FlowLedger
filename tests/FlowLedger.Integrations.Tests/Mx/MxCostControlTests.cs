using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>Minimal controllable clock so cost-control tests need no extra NuGet dependency.</summary>
internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// Cost-control tests for <see cref="FlowLedger.Integrations.Mx.CostControl.MxRefreshCooldown"/>.
/// A second manual refresh within the cooldown window is blocked; after the window it proceeds.
/// Uses a fake clock so the test is fast and deterministic.
/// </summary>
public sealed class MxCostControlTests
{
    private static readonly TenantId Tenant =
        TenantId.From(new Guid("00000000-0000-0000-0000-0000000000aa"));

    private const string MemberId = "USR-1|MBR-1";

    [Fact]
    public async Task First_manual_refresh_is_allowed()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cooldown = MxWireMockFixture.CreateCooldown(TimeSpan.FromMinutes(15), clock);

        var decision = await cooldown.TryBeginManualRefreshAsync(Tenant, MemberId);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Second_manual_refresh_within_window_is_blocked()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cooldown = MxWireMockFixture.CreateCooldown(TimeSpan.FromMinutes(15), clock);

        await cooldown.TryBeginManualRefreshAsync(Tenant, MemberId);
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await cooldown.TryBeginManualRefreshAsync(Tenant, MemberId);

        second.Allowed.Should().BeFalse();
        second.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
        second.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Manual_refresh_after_window_proceeds()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cooldown = MxWireMockFixture.CreateCooldown(TimeSpan.FromMinutes(15), clock);

        await cooldown.TryBeginManualRefreshAsync(Tenant, MemberId);
        clock.Advance(TimeSpan.FromMinutes(20));
        var after = await cooldown.TryBeginManualRefreshAsync(Tenant, MemberId);

        after.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Different_members_have_independent_cooldowns()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cooldown = MxWireMockFixture.CreateCooldown(TimeSpan.FromMinutes(15), clock);

        await cooldown.TryBeginManualRefreshAsync(Tenant, "USR-1|MBR-A");
        var other = await cooldown.TryBeginManualRefreshAsync(Tenant, "USR-1|MBR-B");

        other.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Zero_window_disables_cooldown()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cooldown = MxWireMockFixture.CreateCooldown(TimeSpan.Zero, clock);

        await cooldown.TryBeginManualRefreshAsync(Tenant, MemberId);
        var second = await cooldown.TryBeginManualRefreshAsync(Tenant, MemberId);

        second.Allowed.Should().BeTrue();
    }
}
