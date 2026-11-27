namespace FlowLedger.Application.Features.BillSheet;

/// <summary>
/// Pure expansion of a sheet row into the dates it is due in a month. A monthly row has one date (or none when its due
/// date floats); a weekly or every-N-weeks row has one date per interval that falls in the month, so a fortnightly $55
/// mow shows up two or three times in a month; a twice-monthly row has two. Each date is one "slot" of the month's cell.
/// </summary>
internal static class BillSchedule
{
    /// <summary>The expected payment dates in <paramref name="month"/>; a floating monthly row yields a single null date.</summary>
    public static IReadOnlyList<DateOnly?> Slots(SheetBillData bill, DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));

        switch (bill.Frequency)
        {
            case BillFrequency.Weekly or BillFrequency.EveryNWeeks when bill.AnchorDate is { } anchor:
                {
                    var step = 7 * StepWeeks(bill);
                    // The series is anchor + k * step for any integer k (also before the anchor), so past months line up too.
                    var offset = first.DayNumber - anchor.DayNumber;
                    var k = (int)Math.Ceiling(offset / (double)step);
                    var dates = new List<DateOnly?>();
                    for (var d = anchor.AddDays(k * step); d <= last; d = d.AddDays(step))
                    {
                        if (d >= first)
                        {
                            dates.Add(d);
                        }
                    }

                    return dates;
                }

            case BillFrequency.TwiceMonthly when bill.DueDay is { } d1 && bill.SecondDueDay is { } d2 && d1 < d2:
                return
                [
                    new DateOnly(month.Year, month.Month, Math.Min(d1, last.Day)),
                    new DateOnly(month.Year, month.Month, Math.Min(d2, last.Day)),
                ];

            default:
                return [SheetMatcher.ExpectedDate(bill.DueDay, month)];
        }
    }

    /// <summary>Average payments per month, used where no specific month is in play.</summary>
    public static decimal PerMonth(SheetBillData bill) => bill.Frequency switch
    {
        BillFrequency.Weekly or BillFrequency.EveryNWeeks when bill.AnchorDate is not null => 52m / StepWeeks(bill) / 12m,
        BillFrequency.TwiceMonthly when bill.DueDay is not null && bill.SecondDueDay is not null => 2m,
        _ => 1m,
    };

    /// <summary>True when the row repeats more than once a month and so needs one slot per date.</summary>
    public static bool IsMulti(SheetBillData bill) => PerMonth(bill) > 1m;

    private static int StepWeeks(SheetBillData bill) =>
        bill.Frequency == BillFrequency.Weekly ? 1 : Math.Max(2, bill.IntervalWeeks ?? 2);
}
