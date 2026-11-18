using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features;

public sealed class ForecastBandsTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 17);

    private static IEnumerable<(DateOnly, decimal)> Weekly(params decimal[] amounts) =>
        amounts.Select((a, i) => (AsOf.AddDays(-7 * (amounts.Length - i)).AddDays(1), a));

    [Fact]
    public void Profile_is_mean_and_sample_std_of_weekly_totals()
    {
        var profile = ForecastBands.BuildProfile(Weekly(100, 200, 100, 200, 100, 200, 100, 200, 100, 200), AsOf);

        profile.Weeks.Should().Be(10);
        profile.MeanWeekly.Should().Be(150m);
        profile.StdWeekly.Should().BeApproximately(52.7m, 0.1m);
    }

    [Fact]
    public void Too_little_history_gives_no_profile_and_no_bands()
    {
        var profile = ForecastBands.BuildProfile(Weekly(100, 100, 100), AsOf);

        profile.Should().Be(SpendingProfile.None);
        ForecastBands.Compute([Point(AsOf.AddDays(14), 1000)], AsOf, AsOf.AddDays(30), 1000m, profile).Should().BeEmpty();
    }

    [Fact]
    public void Bands_drift_down_by_mean_and_widen_with_sqrt_of_time()
    {
        var profile = new SpendingProfile(150m, 50m, 20);
        var bands = ForecastBands.Compute(
            [Point(AsOf.AddDays(7), 1000), Point(AsOf.AddDays(28), 1000)], AsOf, AsOf.AddDays(28), 1000m, profile);

        bands.Select(b => b.Date).Should().BeEquivalentTo([AsOf, AsOf.AddDays(7), AsOf.AddDays(28)], o => o.WithStrictOrdering());
        bands[0].Expected.Should().Be(1000m);                    // today: no drift, no spread
        bands[0].Low.Should().Be(1000m);
        bands[1].Expected.Should().Be(850m);
        bands[2].Expected.Should().Be(400m);
        var w1 = bands[1].High - bands[1].Expected;
        var w4 = bands[2].High - bands[2].Expected;
        (w4 / w1).Should().BeApproximately(2m, 0.01m);          // sqrt(4 weeks / 1 week)
        bands[2].Low.Should().BeLessThan(bands[2].Expected);
    }

    [Fact]
    public void With_no_scheduled_events_bands_still_cover_the_horizon_from_the_starting_balance()
    {
        var bands = ForecastBands.Compute([], AsOf, AsOf.AddDays(14), 2000m, new SpendingProfile(100m, 30m, 20));

        bands.Select(b => b.Date).Should().Equal(AsOf, AsOf.AddDays(14));
        bands[1].Expected.Should().Be(1800m);
    }

    [Fact]
    public void Spending_outside_the_window_or_credits_is_ignored()
    {
        var old = (AsOf.AddDays(-400), 9999m);
        var future = (AsOf.AddDays(3), 9999m);
        var profile = ForecastBands.BuildProfile(Weekly(100, 100, 100, 100, 100, 100, 100, 100, 100).Append(old).Append(future), AsOf);

        profile.MeanWeekly.Should().Be(100m);
    }

    private static AggregateForecastPoint Point(DateOnly d, decimal bal) =>
        new() { Date = d, Balance = new Money(bal, Currency.Usd) };
}
