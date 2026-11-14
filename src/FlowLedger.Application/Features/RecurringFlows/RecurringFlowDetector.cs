using FlowLedger.Application.Features.BillSheet;

namespace FlowLedger.Application.Features.RecurringFlows;

/// <summary>One real, posted transaction considered for recurrence detection.</summary>
public sealed record FlowTxn(Guid AccountId, DateOnly Date, decimal Amount, bool IsCredit, string Text);

/// <summary>A recurring pattern found in the transactions, ready to become a <c>RecurringFlow</c>.</summary>
public sealed record DetectedFlow(
    string Key,
    Guid AccountId,
    string Name,
    string Direction,           // "Debit" | "Credit"
    decimal Amount,             // median observed amount
    string AmountModel,         // "Fixed" | "Estimated"
    string Frequency,           // Weekly | EveryNWeeks | TwiceMonthly | Monthly (RecurrenceFrequency names)
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly LastSeen,
    DateOnly NextExpected,
    int Occurrences,
    double Confidence);

/// <summary>
/// Pure detector for recurring income and expenses. Groups transactions by (account, direction, merchant), measures the
/// gaps between occurrences and classifies them as weekly, every-two-weeks, twice-monthly or monthly. High-frequency
/// merchants (fuel, groceries, cafes) never qualify because their gaps are short and irregular; patterns that stopped
/// (a cancelled subscription) are dropped. Confidence blends gap regularity, amount stability and how much history there is.
/// </summary>
public static class RecurringFlowDetector
{
    private const double MinConfidence = 0.6;

    public static IReadOnlyList<DetectedFlow> Detect(IReadOnlyList<FlowTxn> transactions, DateOnly today)
    {
        var result = new List<DetectedFlow>();
        var groups = transactions
            .Where(t => t.Amount > 0 && !string.IsNullOrWhiteSpace(t.Text))
            .GroupBy(t => (t.AccountId, t.IsCredit, Key: RecurringDetector.Key(t.Text)))
            .Where(g => g.Key.Key.Length >= 3);

        foreach (var g in groups)
        {
            var flow = Analyze(g.Key.AccountId, g.Key.IsCredit, g.Key.Key, g.ToList(), today);
            // A pattern that missed two whole periods has stopped (cancelled subscription, ended job): not worth predicting.
            var stopped = flow is not null && today.DayNumber - flow.LastSeen.DayNumber > PeriodDays(flow.Frequency) * 2 + 3;
            if (flow is not null && !stopped && flow.Confidence >= MinConfidence)
            {
                result.Add(flow);
            }
        }

        return result.OrderByDescending(f => f.Confidence).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DetectedFlow? Analyze(Guid accountId, bool isCredit, string key, List<FlowTxn> txns, DateOnly today)
    {
        // One occurrence per date (a split deposit is still one paycheck); amounts on the same day are summed.
        var days = txns.GroupBy(t => t.Date).OrderBy(d => d.Key)
            .Select(d => (Date: d.Key, Amount: d.Sum(x => x.Amount))).ToList();
        if (days.Count < 3)
        {
            return null;
        }

        var gaps = new List<int>();
        for (var i = 1; i < days.Count; i++)
        {
            gaps.Add(days[i].Date.DayNumber - days[i - 1].Date.DayNumber);
        }

        var amounts = days.Select(d => d.Amount).ToList();
        var median = Median(amounts);
        var fixedShare = amounts.Count(a => Math.Abs(a - median) <= median * 0.03m) / (double)amounts.Count;
        var stableShare = amounts.Count(a => Math.Abs(a - median) <= median * 0.35m) / (double)amounts.Count;
        var isFixed = fixedShare >= 0.8;

        var last = days[^1].Date;
        var label = BestLabel(txns);

        // Weekly (≥4 occurrences). Variable amounts only for income: weekly expense with varying amounts is shopping.
        var weekly = Share(gaps, 6, 8);
        if (weekly >= 0.7 && days.Count >= 4 && (isFixed || stableShare >= 0.7 && isCredit))
        {
            var dow = ModeDayOfWeek(days.Select(d => d.Date));
            return Build(key, accountId, isCredit, label, median, isFixed, "Weekly", null, null, null, dow,
                last, NextWeekly(last, 7, today), days.Count, weekly, expectedN: 6);
        }

        // Every two weeks (≥3).
        var biweekly = Share(gaps, 13, 15);
        if (biweekly >= 0.7 && (isFixed || stableShare >= 0.7))
        {
            var dow = ModeDayOfWeek(days.Select(d => d.Date));
            return Build(key, accountId, isCredit, label, median, isFixed, "EveryNWeeks", null, null, 2, dow,
                last, NextWeekly(last, 14, today), days.Count, biweekly, expectedN: 4);
        }

        // Twice a month on two stable days (≥4 occurrences covering ≥2 months).
        if (TryTwiceMonthly(days.Select(d => d.Date).ToList(), out var d1, out var d2) && days.Count >= 4 && (isFixed || stableShare >= 0.7))
        {
            return Build(key, accountId, isCredit, label, median, isFixed, "TwiceMonthly", d1, d2, null, null,
                last, NextMonthly(last, today, d1, d2), days.Count, 0.95, expectedN: 4);
        }

        // Monthly: gaps of roughly a month, at most one per month, with a stable day or a stable amount.
        var monthly = Share(gaps, 26, 35);
        if (monthly >= 0.7)
        {
            var monthDays = days.Select(d => d.Date.Day).ToList();
            var medDay = (int)Math.Round(Median(monthDays.Select(x => (decimal)x).ToList()));
            var dayStable = monthDays.Count(x => Math.Abs(x - medDay) <= 4) >= monthDays.Count * 0.7;
            if (dayStable && stableShare >= 0.6)
            {
                return Build(key, accountId, isCredit, label, median, isFixed, "Monthly", Math.Min(medDay, 28), null, null, null,
                    last, NextMonthly(last, today, Math.Min(medDay, 28), null), days.Count, monthly, expectedN: 3);
            }
        }

        return null;
    }

    private static DetectedFlow? Build(
        string key, Guid accountId, bool isCredit, string label, decimal amount, bool isFixed, string frequency,
        int? day, int? day2, int? weeks, string? dow, DateOnly last, DateOnly next, int count, double consistency, int expectedN)
    {
        var amountScore = isFixed ? 1.0 : 0.8;
        var countScore = Math.Min(1.0, count / (double)expectedN);
        var confidence = Math.Round(0.5 * consistency + 0.3 * amountScore + 0.2 * countScore, 2);
        return new DetectedFlow(
            Key: $"{accountId:N}|{(isCredit ? "C" : "D")}|{key}",
            AccountId: accountId,
            Name: label,
            Direction: isCredit ? "Credit" : "Debit",
            Amount: Math.Round(amount, 2),
            AmountModel: isFixed ? "Fixed" : "Estimated",
            Frequency: frequency,
            DayOfMonth: day,
            SecondDayOfMonth: day2,
            IntervalWeeks: weeks,
            AnchorDayOfWeek: dow,
            LastSeen: last,
            NextExpected: next,
            Occurrences: count,
            Confidence: confidence);
    }

    private static int PeriodDays(string frequency) =>
        frequency switch { "Weekly" => 7, "EveryNWeeks" => 14, "TwiceMonthly" => 16, _ => 31 };

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

    /// <summary>Display name: the most common text up to the first digit run (references and dates vary).</summary>
    private static string BestLabel(List<FlowTxn> txns)
    {
        var raw = txns.GroupBy(t => RecurringDetector.MatchTextFor(t.Text.Trim()))
            .OrderByDescending(g => g.Count()).First().Key;
        var collapsed = string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 80 ? collapsed[..80] : collapsed;
    }

    private static decimal Median(List<decimal> values)
    {
        var s = values.OrderBy(x => x).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }
}
