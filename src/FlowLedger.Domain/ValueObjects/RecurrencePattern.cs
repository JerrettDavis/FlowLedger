namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Describes how a recurring flow repeats. Encapsulates the schedule model from PLAN.md §6.5.
/// The pattern is used by the domain to generate <see cref="Aggregates.PlannedFlowOccurrence"/>
/// dates; it never performs I/O or complex calendar arithmetic itself — that is deferred to
/// the Forecasting Engine (Milestone 4).
/// </summary>
public sealed record RecurrencePattern
{
    public RecurrenceFrequency Frequency { get; }

    /// <summary>
    /// Day-of-month (1–31) used by <see cref="RecurrenceFrequency.Monthly"/> and
    /// <see cref="RecurrenceFrequency.TwiceMonthly"/>.
    /// For TwiceMonthly this is the first occurrence day; SecondDayOfMonth holds the second.
    /// </summary>
    public int? DayOfMonth { get; }

    /// <summary>Second day-of-month for TwiceMonthly patterns.</summary>
    public int? SecondDayOfMonth { get; }

    /// <summary>Interval for Every-N-Weeks patterns.</summary>
    public int? IntervalWeeks { get; }

    /// <summary>Day-of-week anchor for Weekly / EveryNWeeks patterns.</summary>
    public DayOfWeek? AnchorDayOfWeek { get; }

    private RecurrencePattern(
        RecurrenceFrequency frequency,
        int? dayOfMonth,
        int? secondDayOfMonth,
        int? intervalWeeks,
        DayOfWeek? anchorDayOfWeek)
    {
        Frequency = frequency;
        DayOfMonth = dayOfMonth;
        SecondDayOfMonth = secondDayOfMonth;
        IntervalWeeks = intervalWeeks;
        AnchorDayOfWeek = anchorDayOfWeek;
    }

    // ── Factories ────────────────────────────────────────────────────────────

    public static RecurrencePattern Daily() =>
        new(RecurrenceFrequency.Daily, null, null, null, null);

    public static RecurrencePattern Weekly(DayOfWeek dayOfWeek) =>
        new(RecurrenceFrequency.Weekly, null, null, null, dayOfWeek);

    public static RecurrencePattern EveryNWeeks(int weeks, DayOfWeek dayOfWeek)
    {
        if (weeks < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(weeks), "Interval must be at least 2 weeks.");
        }

        return new(RecurrenceFrequency.EveryNWeeks, null, null, weeks, dayOfWeek);
    }

    public static RecurrencePattern TwiceMonthly(int firstDay, int secondDay)
    {
        GuardDayOfMonth(firstDay, nameof(firstDay));
        GuardDayOfMonth(secondDay, nameof(secondDay));
        if (firstDay >= secondDay)
        {
            throw new ArgumentException("firstDay must be less than secondDay.", nameof(firstDay));
        }

        return new(RecurrenceFrequency.TwiceMonthly, firstDay, secondDay, null, null);
    }

    public static RecurrencePattern Monthly(int dayOfMonth)
    {
        GuardDayOfMonth(dayOfMonth, nameof(dayOfMonth));
        return new(RecurrenceFrequency.Monthly, dayOfMonth, null, null, null);
    }

    public static RecurrencePattern LastBusinessDay() =>
        new(RecurrenceFrequency.LastBusinessDay, null, null, null, null);

    private static void GuardDayOfMonth(int day, string paramName)
    {
        if (day is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(paramName, $"Day-of-month must be between 1 and 31, got {day}.");
        }
    }

    public override string ToString() => Frequency switch
    {
        RecurrenceFrequency.Monthly => $"Monthly on day {DayOfMonth}",
        RecurrenceFrequency.TwiceMonthly => $"Twice monthly on days {DayOfMonth} and {SecondDayOfMonth}",
        RecurrenceFrequency.Weekly => $"Weekly on {AnchorDayOfWeek}",
        RecurrenceFrequency.EveryNWeeks => $"Every {IntervalWeeks} weeks on {AnchorDayOfWeek}",
        _ => Frequency.ToString()
    };
}

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    EveryNWeeks,
    TwiceMonthly,
    Monthly,
    LastBusinessDay,
}
