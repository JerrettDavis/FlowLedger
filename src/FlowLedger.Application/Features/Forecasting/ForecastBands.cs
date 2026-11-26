using FlowLedger.Domain.ValueObjects;
namespace FlowLedger.Application.Features.Forecasting;

/// <summary>The household's typical week of spending that no recurring flow accounts for.</summary>
public sealed record SpendingProfile(decimal MeanWeekly, decimal StdWeekly, int Weeks)
{
    public static SpendingProfile None { get; } = new(0, 0, 0);
}

/// <summary>Expected balance and an 80% range on one date, once everyday (unscheduled) spending is accounted for.</summary>
public sealed record ForecastBandPoint(DateOnly Date, decimal Expected, decimal Low, decimal High);

/// <summary>
/// Pure uncertainty layer on top of the deterministic forecast. The engine projects scheduled flows only, so it ignores
/// groceries, fuel, dining and shopping. This estimates that unscheduled weekly spend from history (mean and spread of
/// weekly totals) and widens it into an expected line with an 80% range that grows with the square root of time
/// (independent weeks). No I/O, no randomness: same inputs, same bands.
/// </summary>
public static class ForecastBands
{
    public const int MinWeeks = 8;
    public const int HistoryWeeks = 26;

    /// <summary>1.2816 is the two-sided 80% z-score (10th to 90th percentile).</summary>
    public const double Z80 = 1.2816;

    /// <param name="unscheduledDebits">Debits not explained by a recurring flow, transfers already removed.</param>
    /// <param name="asOf">Weeks are counted back from here, in whole 7-day blocks ending the day before.</param>
    public static SpendingProfile BuildProfile(IEnumerable<(DateOnly Date, decimal Amount)> unscheduledDebits, DateOnly asOf)
    {
        var start = asOf.AddDays(-7 * HistoryWeeks);
        var totals = new decimal[HistoryWeeks];
        var earliest = DateOnly.MaxValue;
        foreach (var (date, amount) in unscheduledDebits)
        {
            if (date < start || date >= asOf || amount <= 0)
            {
                continue;
            }

            totals[(date.DayNumber - start.DayNumber) / 7] += amount;
            earliest = date < earliest ? date : earliest;
        }

        // Only count weeks the data actually covers (a new account has no history before it opened).
        var firstWeek = earliest == DateOnly.MaxValue ? HistoryWeeks : (earliest.DayNumber - start.DayNumber) / 7;
        var weeks = totals.Skip(firstWeek).ToArray();
        if (weeks.Length < MinWeeks)
        {
            return SpendingProfile.None;
        }

        var mean = weeks.Average();
        var variance = weeks.Sum(w => (double)((w - mean) * (w - mean))) / (weeks.Length - 1);
        return new SpendingProfile(MoneyRounding.Round(mean, 2), MoneyRounding.Round((decimal)Math.Sqrt(variance), 2), weeks.Length);
    }

    /// <param name="startingTotal">Combined starting balance, used before the first scheduled event (or when none exist).</param>
    /// <param name="horizonEnd">The last day to cover, so bands exist even when no flow is scheduled.</param>
    public static IReadOnlyList<ForecastBandPoint> Compute(
        IReadOnlyList<AggregateForecastPoint> aggregate, DateOnly asOf, DateOnly horizonEnd, decimal startingTotal, SpendingProfile profile)
    {
        if (profile.Weeks < MinWeeks)
        {
            return [];
        }

        // Event-stepped scheduled balance, plus the horizon's own start and end so the range always reaches the edge.
        var steps = aggregate.OrderBy(a => a.Date).ToList();
        var dates = steps.Select(a => a.Date).Append(asOf).Append(horizonEnd).Where(d => d >= asOf && d <= horizonEnd).Distinct().OrderBy(d => d);
        var points = new List<ForecastBandPoint>();
        foreach (var date in dates)
        {
            var balance = steps.LastOrDefault(a => a.Date <= date)?.Balance.Amount ?? startingTotal;
            var days = date.DayNumber - asOf.DayNumber;
            var weeksAhead = days / 7.0;
            var expected = balance - profile.MeanWeekly * (decimal)weeksAhead;
            var spread = (decimal)(Z80 * (double)profile.StdWeekly * Math.Sqrt(weeksAhead));
            points.Add(new ForecastBandPoint(date, MoneyRounding.Round(expected, 2), MoneyRounding.Round(expected - spread, 2), MoneyRounding.Round(expected + spread, 2)));
        }

        return points;
    }
}
