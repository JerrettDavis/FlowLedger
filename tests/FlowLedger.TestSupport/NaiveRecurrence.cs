using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.TestSupport;

/// <summary>
/// Deliberately dumb reference implementation of recurrence expansion: walk every calendar day in the window and ask
/// "does the pattern fire today?". Used as the oracle in differential property tests against the production
/// <see cref="RecurrenceExpander"/>, which steps by week / month instead. Anything clever belongs in production, not here.
/// </summary>
public static class NaiveRecurrence
{
    public static List<DateOnly> Expand(RecurrencePattern pattern, DateOnly flowStart, DateOnly? flowEndExclusive, DateOnly horizonStart, DateOnly horizonEndInclusive)
    {
        var from = flowStart > horizonStart ? flowStart : horizonStart;
        var to = flowEndExclusive is { } e && e.AddDays(-1) < horizonEndInclusive ? e.AddDays(-1) : horizonEndInclusive;
        var result = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (Fires(pattern, flowStart, d))
            {
                result.Add(d);
            }
        }

        return result;
    }

    public static bool Fires(RecurrencePattern p, DateOnly flowStart, DateOnly day)
    {
        var dim = DateTime.DaysInMonth(day.Year, day.Month);
        switch (p.Frequency)
        {
            case RecurrenceFrequency.Daily:
                return true;
            case RecurrenceFrequency.Weekly:
                return day.DayOfWeek == p.AnchorDayOfWeek && day >= flowStart;
            case RecurrenceFrequency.EveryNWeeks:
            {
                var first = flowStart;
                while (first.DayOfWeek != p.AnchorDayOfWeek)
                {
                    first = first.AddDays(1);
                }

                return day >= first && (day.DayNumber - first.DayNumber) % (7 * p.IntervalWeeks!.Value) == 0;
            }

            case RecurrenceFrequency.Monthly:
                return day.Day == Math.Min(p.DayOfMonth!.Value, dim);
            case RecurrenceFrequency.TwiceMonthly:
                return day.Day == Math.Min(p.DayOfMonth!.Value, dim) || day.Day == Math.Min(p.SecondDayOfMonth!.Value, dim);
            case RecurrenceFrequency.LastBusinessDay:
            {
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    return false;
                }

                // Last business day: no later weekday remains in this month.
                for (var later = day.AddDays(1); later.Month == day.Month; later = later.AddDays(1))
                {
                    if (later.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(p));
        }
    }
}
