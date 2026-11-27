namespace FlowLedger.Application.Features.RecurringFlows;

internal static partial class RecurringFlowDetector
{
    private static int DayDistance(int a, int b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, 30 - d);
    }

    private static int PeriodDays(DetectedFlow flow) =>
        flow.Frequency switch { "Weekly" => 7, "EveryNWeeks" => 7 * (flow.IntervalWeeks ?? 2), "TwiceMonthly" => 16, _ => 31 };

    private static double Share(List<int> gaps, int min, int max) =>
        gaps.Count(g => g >= min && g <= max) / (double)gaps.Count;

    private static string ModeDayOfWeek(IEnumerable<DateOnly> dates) =>
        dates.GroupBy(d => d.DayOfWeek).OrderByDescending(g => g.Count()).First().Key.ToString();

    private static DateOnly NextWeekly(DateOnly last, int stepDays, DateOnly today)
    {
        var next = last.AddDays(stepDays);
        while (next < today)
        {
            next = next.AddDays(stepDays);
        }

        return next;
    }

    private static DateOnly NextMonthly(DateOnly last, DateOnly today, int day, int? day2)
    {
        var candidates = new List<DateOnly>();
        var cursor = new DateOnly(last.Year, last.Month, 1);
        for (var i = 0; i < 6; i++)
        {
            var month = cursor.AddMonths(i);
            foreach (var d in new[] { day, day2 ?? day }.Distinct())
            {
                var dom = Math.Min(d, DateTime.DaysInMonth(month.Year, month.Month));
                var date = new DateOnly(month.Year, month.Month, dom);
                if (date > last && date >= today)
                {
                    candidates.Add(date);
                }
            }
        }

        return candidates.OrderBy(c => c).FirstOrDefault(last.AddMonths(1));
    }

    private static bool TryTwiceMonthly(List<DateOnly> dates, out int d1, out int d2)
    {
        d1 = d2 = 0;
        var byMonth = dates.GroupBy(d => (d.Year, d.Month)).ToList();
        if (byMonth.Count < 2)
        {
            return false;
        }

        // Interior months must each have exactly two occurrences (the first/last month may be partial).
        var interior = byMonth.Skip(1).Take(Math.Max(0, byMonth.Count - 2)).ToList();
        if (interior.Any(m => m.Count() != 2) || byMonth.Any(m => m.Count() > 2))
        {
            return false;
        }

        var firsts = byMonth.Where(m => m.Count() == 2).Select(m => m.Min(d => d.Day)).ToList();
        var seconds = byMonth.Where(m => m.Count() == 2).Select(m => m.Max(d => d.Day)).ToList();
        if (firsts.Count == 0)
        {
            return false;
        }

        var first = (int)Median(firsts.Select(x => (decimal)x).ToList());
        var second = (int)Median(seconds.Select(x => (decimal)x).ToList());
        d1 = first;
        d2 = second;
        return second - first >= 10
            && firsts.All(x => Math.Abs(x - first) <= 2)
            && seconds.All(x => Math.Abs(x - second) <= 2);
    }

    /// <summary>
    /// Display name: an alias the user gave it (mixed case) as-is, otherwise the normalized merchant, which drops the
    /// processor prefix, reference numbers and state.
    /// </summary>
    private static string BestLabel(List<FlowTxn> txns, string normalizedName)
    {
        var raw = txns.GroupBy(t => t.Text.Trim()).OrderByDescending(g => g.Count()).First().Key;
        var text = raw.Any(char.IsLower) ? raw : normalizedName;
        var collapsed = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 80 ? collapsed[..80] : collapsed;
    }

    private static decimal Median(List<decimal> values)
    {
        var s = values.OrderBy(x => x).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }
}
