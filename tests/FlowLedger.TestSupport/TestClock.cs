namespace FlowLedger.TestSupport;

/// <summary>
/// The fixed "now" for tests that need a date but do not exercise the system clock. Deliberately the last second of
/// a 31-day month, so month-end and midnight-boundary bugs show up on every run instead of one day a month.
/// Tests must not read <c>DateTime.UtcNow</c>/<c>Now</c> (a ratchet test in Architecture.Tests enforces this).
/// </summary>
public static class TestClock
{
    public static readonly DateTimeOffset Instant = new(2026, 1, 31, 23, 59, 59, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 1, 31);

    /// <summary>A <see cref="TimeProvider"/> frozen at <see cref="Instant"/> (UTC), for the explicit time every aggregate factory and handler takes.</summary>
    public static TimeProvider Provider { get; } = new Frozen(Instant);

    /// <summary>A provider frozen at <paramref name="instant"/> (UTC).</summary>
    public static TimeProvider At(DateTimeOffset instant) => new Frozen(instant);

    private sealed class Frozen(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
