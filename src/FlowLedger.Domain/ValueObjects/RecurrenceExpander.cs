namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Pure, deterministic helper that expands a <see cref="RecurrencePattern"/> into
/// occurrence dates within a given window.
///
/// Rules:
/// - The start date of the recurring flow's active window is treated as the
///   "anchor" for week-based patterns (EveryNWeeks, Weekly).
/// - Daily: every calendar day from window start through the last date of the
///   forecast horizon.
/// - Weekly: every occurrence of <see cref="RecurrencePattern.AnchorDayOfWeek"/>
///   on or after the flow's start date, within the horizon.
/// - EveryNWeeks: every N weeks starting from the first occurrence of
///   <see cref="RecurrencePattern.AnchorDayOfWeek"/> on or after the flow start.
/// - TwiceMonthly: two fixed days per month (DayOfMonth + SecondDayOfMonth),
///   clamped to the actual last day of the month when the specified day
///   exceeds the month length (e.g. day 31 in February → Feb 28/29).
/// - Monthly: one fixed day per month, with the same month-end clamping.
/// - LastBusinessDay: the last Monday–Friday of each calendar month, stepping
///   backward from the last calendar day. Saturdays → Friday; Sundays → Friday.
///
/// This class is intentionally free of any I/O, randomness, or wall-clock calls.
/// The as-of / horizon window is an explicit parameter so the output is
/// fully reproducible (required by PLAN.md §11 determinism requirement).
/// </summary>
public static class RecurrenceExpander
{
    /// <summary>
    /// Expands <paramref name="pattern"/> into all occurrence dates that:
    /// <list type="bullet">
    ///   <item>fall within <paramref name="horizon"/></item>
    ///   <item>are on or after <paramref name="flowStart"/> (the flow's ActiveWindow.Start)</item>
    ///   <item>are strictly before <paramref name="flowEnd"/> if not null (the flow's ActiveWindow.End)</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<DateOnly> Expand(
        RecurrencePattern pattern,
        DateOnly flowStart,
        DateOnly? flowEnd,
        DateOnlyRange horizon)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(horizon);

        // Effective generation window: intersection of flow active window and horizon.
        //
        // horizon.End is treated as INCLUSIVE (generate up to and including that date).
        // flowEnd is EXCLUSIVE per the DateOnlyRange.Contains convention (the flow is active
        // on [flowStart, flowEnd) ), so we convert it to an inclusive bound by subtracting one day.
        var windowStart = flowStart > horizon.Start ? flowStart : horizon.Start;

        if (!horizon.End.HasValue)
            throw new ArgumentException("A finite horizon end is required for expansion.", nameof(horizon));

        var horizonInclusiveEnd = horizon.End.Value;

        // Convert exclusive flowEnd → inclusive max date for this flow
        DateOnly? flowInclusiveEnd = flowEnd.HasValue ? flowEnd.Value.AddDays(-1) : null;

        // Take the minimum of horizon end and flow inclusive end
        DateOnly effectiveEnd = flowInclusiveEnd.HasValue
            ? (flowInclusiveEnd.Value < horizonInclusiveEnd ? flowInclusiveEnd.Value : horizonInclusiveEnd)
            : horizonInclusiveEnd;

        var end = effectiveEnd;

        if (windowStart > end)
            return Array.Empty<DateOnly>();

        return pattern.Frequency switch
        {
            RecurrenceFrequency.Daily          => ExpandDaily(windowStart, end),
            RecurrenceFrequency.Weekly         => ExpandWeekly(pattern, flowStart, windowStart, end),
            RecurrenceFrequency.EveryNWeeks    => ExpandEveryNWeeks(pattern, flowStart, windowStart, end),
            RecurrenceFrequency.TwiceMonthly   => ExpandTwiceMonthly(pattern, windowStart, end),
            RecurrenceFrequency.Monthly        => ExpandMonthly(pattern, windowStart, end),
            RecurrenceFrequency.LastBusinessDay => ExpandLastBusinessDay(windowStart, end),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern.Frequency, "Unknown recurrence frequency.")
        };
    }

    // ── Private expansion helpers ────────────────────────────────────────────

    private static IReadOnlyList<DateOnly> ExpandDaily(DateOnly start, DateOnly end)
    {
        var dates = new List<DateOnly>();
        var current = start;
        while (current <= end)
        {
            dates.Add(current);
            current = current.AddDays(1);
        }
        return dates;
    }

    private static IReadOnlyList<DateOnly> ExpandWeekly(
        RecurrencePattern pattern, DateOnly flowStart, DateOnly windowStart, DateOnly end)
    {
        var anchorDay = pattern.AnchorDayOfWeek
            ?? throw new InvalidOperationException("Weekly pattern requires AnchorDayOfWeek.");

        // Find first occurrence of anchorDay on or after flowStart
        var firstAnchor = NextOrSameDayOfWeek(flowStart, anchorDay);

        var dates = new List<DateOnly>();
        var current = firstAnchor;
        while (current <= end)
        {
            if (current >= windowStart)
                dates.Add(current);
            current = current.AddDays(7);
        }
        return dates;
    }

    private static IReadOnlyList<DateOnly> ExpandEveryNWeeks(
        RecurrencePattern pattern, DateOnly flowStart, DateOnly windowStart, DateOnly end)
    {
        var anchorDay = pattern.AnchorDayOfWeek
            ?? throw new InvalidOperationException("EveryNWeeks pattern requires AnchorDayOfWeek.");
        var weeks = pattern.IntervalWeeks
            ?? throw new InvalidOperationException("EveryNWeeks pattern requires IntervalWeeks.");

        // Anchor: first occurrence of anchorDay on or after flowStart
        var firstAnchor = NextOrSameDayOfWeek(flowStart, anchorDay);

        var dates = new List<DateOnly>();
        var current = firstAnchor;
        while (current <= end)
        {
            if (current >= windowStart)
                dates.Add(current);
            current = current.AddDays(7 * weeks);
        }
        return dates;
    }

    private static IReadOnlyList<DateOnly> ExpandTwiceMonthly(
        RecurrencePattern pattern, DateOnly windowStart, DateOnly end)
    {
        var day1 = pattern.DayOfMonth
            ?? throw new InvalidOperationException("TwiceMonthly pattern requires DayOfMonth.");
        var day2 = pattern.SecondDayOfMonth
            ?? throw new InvalidOperationException("TwiceMonthly pattern requires SecondDayOfMonth.");

        var dates = new List<DateOnly>();
        var month = new DateOnly(windowStart.Year, windowStart.Month, 1);
        var endMonth = new DateOnly(end.Year, end.Month, 1);

        while (month <= endMonth)
        {
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
            var d1 = new DateOnly(month.Year, month.Month, Math.Min(day1, daysInMonth));
            var d2 = new DateOnly(month.Year, month.Month, Math.Min(day2, daysInMonth));

            if (d1 >= windowStart && d1 <= end) dates.Add(d1);
            if (d2 >= windowStart && d2 <= end && d2 != d1) dates.Add(d2);

            month = month.AddMonths(1);
        }

        dates.Sort();
        return dates;
    }

    private static IReadOnlyList<DateOnly> ExpandMonthly(
        RecurrencePattern pattern, DateOnly windowStart, DateOnly end)
    {
        var day = pattern.DayOfMonth
            ?? throw new InvalidOperationException("Monthly pattern requires DayOfMonth.");

        var dates = new List<DateOnly>();
        var month = new DateOnly(windowStart.Year, windowStart.Month, 1);
        var endMonth = new DateOnly(end.Year, end.Month, 1);

        while (month <= endMonth)
        {
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
            var occurrence = new DateOnly(month.Year, month.Month, Math.Min(day, daysInMonth));
            if (occurrence >= windowStart && occurrence <= end)
                dates.Add(occurrence);

            month = month.AddMonths(1);
        }
        return dates;
    }

    private static IReadOnlyList<DateOnly> ExpandLastBusinessDay(DateOnly windowStart, DateOnly end)
    {
        var dates = new List<DateOnly>();
        var month = new DateOnly(windowStart.Year, windowStart.Month, 1);
        var endMonth = new DateOnly(end.Year, end.Month, 1);

        while (month <= endMonth)
        {
            var lbd = LastBusinessDayOfMonth(month.Year, month.Month);
            if (lbd >= windowStart && lbd <= end)
                dates.Add(lbd);
            month = month.AddMonths(1);
        }
        return dates;
    }

    // ── Pure calendar helpers ────────────────────────────────────────────────

    /// <summary>
    /// Returns the last Monday–Friday of the specified month.
    /// If the last calendar day is Saturday, returns the Friday before it.
    /// If Sunday, returns the Friday two days before it.
    /// </summary>
    internal static DateOnly LastBusinessDayOfMonth(int year, int month)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.DayOfWeek switch
        {
            DayOfWeek.Saturday => last.AddDays(-1),
            DayOfWeek.Sunday   => last.AddDays(-2),
            _                  => last
        };
    }

    /// <summary>
    /// Returns the first date on or after <paramref name="from"/> that falls on
    /// <paramref name="targetDay"/>.
    /// </summary>
    internal static DateOnly NextOrSameDayOfWeek(DateOnly from, DayOfWeek targetDay)
    {
        var daysAhead = ((int)targetDay - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(daysAhead);
    }
}
