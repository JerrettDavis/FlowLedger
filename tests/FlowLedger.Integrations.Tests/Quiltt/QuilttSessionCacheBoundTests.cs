using FlowLedger.Integrations.Quiltt;

namespace FlowLedger.Integrations.Tests.Quiltt;

/// <summary>#179: the per-profile session cache is bounded, preferring to drop expired then soonest-to-expire entries.</summary>
public sealed class QuilttSessionCacheBoundTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cache_stays_at_or_below_the_cap_after_inserting_ten_times_as_many_distinct_profiles()
    {
        const int cap = 50;
        var cache = new QuilttSessionCache(new Clock(T0), cap);

        for (var i = 0; i < cap * 10; i++)
        {
            cache.Put(new QuilttSession("tok" + i, "p" + i, T0.AddMinutes(30 + i % 20)));
        }

        cache.Count.Should().BeLessThanOrEqualTo(cap);
    }

    [Fact]
    public void Expired_entries_are_dropped_before_live_ones_when_the_cap_is_hit()
    {
        var clock = new Clock(T0);
        var cache = new QuilttSessionCache(clock, maxEntries: 2);
        cache.Put(new QuilttSession("a", "pa", T0.AddMinutes(30)));
        cache.Put(new QuilttSession("b", "pb", T0.AddMinutes(40)));
        clock.Now = T0.AddMinutes(25);   // pa is now inside the 10-minute expiry skew, i.e. no longer usable

        cache.Put(new QuilttSession("c", "pc", clock.Now.AddMinutes(45)));

        cache.TryGet("pb").Should().NotBeNull();
        cache.TryGet("pc").Should().NotBeNull();
        cache.Count.Should().BeLessThanOrEqualTo(2);
    }
}
