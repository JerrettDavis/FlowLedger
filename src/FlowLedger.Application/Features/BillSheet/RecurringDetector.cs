namespace FlowLedger.Application.Features.BillSheet;

/// <summary>A monthly-repeating expense found in the transactions, ready to become a sheet row.</summary>
public sealed record DetectedBill(string Name, string MatchText, int? DueDay, decimal Estimated, decimal Actual);

/// <summary>
/// Pure detector: finds merchants that charge roughly once a month. A merchant qualifies when it
/// appears in at least <see cref="MinMonths"/> distinct months, never more than twice in a month,
/// and either the amount or the day of month is consistent (utilities vary in amount, not in day).
/// Frequent-purchase merchants (groceries, fuel, cafes) fail the per-month or consistency rules.
/// </summary>
public static class RecurringDetector
{
    public const int MinMonths = 3;
    private const decimal AmountTolerance = 0.35m;
    private const int DayTolerance = 4;

    public static IReadOnlyList<DetectedBill> Detect(IReadOnlyList<SheetTxn> transactions)
    {
        var result = new List<DetectedBill>();
        var groups = transactions
            .Where(t => !string.IsNullOrWhiteSpace(t.Label ?? t.Text))
            .GroupBy(t => Key(t.Label ?? t.Text))
            .Where(g => g.Key.Length >= 3);

        foreach (var g in groups)
        {
            var byMonth = g.GroupBy(t => (t.Date.Year, t.Date.Month)).ToList();
            if (byMonth.Count < MinMonths || byMonth.Any(m => m.Count() > 2))
            {
                continue;
            }

            var amounts = g.Select(t => t.Amount).OrderBy(a => a).ToList();
            var medianAmount = Median(amounts);
            var amountStable = medianAmount > 0
                && g.Count(t => Math.Abs(t.Amount - medianAmount) <= medianAmount * AmountTolerance) >= g.Count() * 0.7;

            var days = g.Select(t => t.Date.Day).OrderBy(d => d).ToList();
            var medianDay = (int)Math.Round(Median(days.Select(d => (decimal)d).ToList()));
            var dayStable = g.All(t => Math.Abs(t.Date.Day - medianDay) <= DayTolerance);

            if (!amountStable && !dayStable)
            {
                continue;
            }

            var label = g.GroupBy(t => (t.Label ?? t.Text).Trim()).OrderByDescending(x => x.Count()).First().Key;
            var latest = g.OrderByDescending(t => t.Date).First();
            result.Add(new DetectedBill(
                label,
                MatchTextFor(label),
                dayStable ? medianDay : null,
                Math.Round(medianAmount, 2),
                latest.Amount));
        }

        return result.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Grouping key: letters only, so "ATT*BILL 1234" and "ATT*BILL 5678" are one merchant.</summary>
    internal static string Key(string text)
        => new(text.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Text up to the first digit (card references and dates vary), falling back to the whole label.</summary>
    internal static string MatchTextFor(string label)
    {
        var cut = label.IndexOfAny("0123456789".ToCharArray());
        var head = cut > 0 ? label[..cut].Trim(' ', '*', '#', '-', '_') : label.Trim();
        return head.Length >= 3 ? head : label.Trim();
    }

    private static decimal Median(List<decimal> sorted)
    {
        var s = sorted.OrderBy(x => x).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }
}
