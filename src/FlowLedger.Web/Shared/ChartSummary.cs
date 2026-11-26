using System.Globalization;

namespace FlowLedger.Web.Shared;

/// <summary>One labelled value in a chart's "View data" table.</summary>
public sealed record ChartRow(string Label, string Value);

/// <summary>One group of the allocation bar with its share of the total.</summary>
public sealed record AllocationItem(string Title, string Tone, double Percent);

/// <summary>Builds the text alternatives that make charts and progress bars readable without colour or sight.</summary>
public static class ChartSummary
{
    /// <summary>"Spending: Rent 900.00, Groceries 120.00 and 3 more."</summary>
    public static string Categories(string subject, IEnumerable<(string Label, decimal Value)> rows, Func<decimal, string> format, int top = 5)
    {
        var all = rows.OrderByDescending(r => Math.Abs(r.Value)).ToList();
        if (all.Count == 0)
        {
            return $"{subject}: no data.";
        }

        var shown = string.Join(", ", all.Take(top).Select(r => $"{r.Label} {format(r.Value)}"));
        var more = all.Count - top;
        return more > 0 ? $"{subject}: {shown} and {more} more." : $"{subject}: {shown}.";
    }

    /// <summary>"Net worth: from Jan 100.00 to Mar 130.00, up 30.00 (high 160.00, low 100.00)."</summary>
    public static string Trend(string subject, IReadOnlyList<(string Label, decimal Value)> points, Func<decimal, string> format)
    {
        if (points.Count == 0)
        {
            return $"{subject}: no data.";
        }

        var first = points[0];
        var last = points[^1];
        var change = last.Value - first.Value;
        var direction = change > 0 ? $"up {format(change)}" : change < 0 ? $"down {format(-change)}" : "unchanged";
        var high = points.Max(p => p.Value);
        var low = points.Min(p => p.Value);
        return $"{subject}: from {first.Label} {format(first.Value)} to {last.Label} {format(last.Value)}, {direction} (high {format(high)}, low {format(low)}).";
    }

    /// <summary>Describes multi-series line charts: for each series, start, end, high and low.</summary>
    public static string Series(string subject, IReadOnlyList<string> labels, IEnumerable<(string Name, double[] Data)> series, Func<double, string> format)
    {
        var parts = new List<string>();
        foreach (var (name, data) in series)
        {
            if (data.Length == 0)
            {
                continue;
            }

            var first = labels.Count > 0 ? labels[0] : "start";
            var last = labels.Count > 0 ? labels[Math.Min(labels.Count, data.Length) - 1] : "end";
            parts.Add($"{name} goes from {format(data[0])} at {first} to {format(data[^1])} at {last} (high {format(data.Max())}, low {format(data.Min())})");
        }

        return parts.Count == 0 ? $"{subject}: no data." : $"{subject}: {string.Join("; ", parts)}.";
    }

    /// <summary>The aria-valuetext of a progress bar, e.g. "Groceries: 82% of budget used".</summary>
    public static string ProgressText(string name, double percent, string suffix) =>
        $"{name}: {Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}% {suffix}";
}
