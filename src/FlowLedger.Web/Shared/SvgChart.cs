using Microsoft.AspNetCore.Components;

namespace FlowLedger.Web.Shared;

/// <summary>
/// Static SVG chart builder for theme-aware balance projection graphs.
/// Returns MarkupString with inline CSS variables for color integration.
/// </summary>
public static class SvgChart
{
    /// <summary>
    /// Build a balance projection chart as an SVG MarkupString.
    ///
    /// Parameters:
    /// - points: List of (date, balance) tuples. Must have at least 2 points.
    /// - zeroLine: Optional horizontal reference line (e.g., 0m for break-even).
    /// - breaches: Optional list of dates where balance violated a constraint.
    /// - lowWaterMark: Optional (date, balance) pair marking the lowest point.
    ///
    /// Colors are driven by CSS variables (--mud-palette-*) so the chart respects theme.
    /// </summary>
    public static MarkupString BuildBalanceProjection(
        IReadOnlyList<(DateOnly date, decimal balance)> points,
        decimal? zeroLine = 0m,
        IReadOnlyList<DateOnly>? breaches = null,
        (DateOnly date, decimal balance)? lowWaterMark = null)
    {
        if (points == null || points.Count < 2)
        {
            return new MarkupString(string.Empty);
        }

        // ViewBox 800x220, compute min/max with padding
        const int W = 800, H = 220, padL = 70, padR = 70, padT = 20, padB = 35;
        int plotW = W - padL - padR;
        int plotH = H - padT - padB;

        decimal minBal = points.Min(p => p.balance);
        decimal maxBal = points.Max(p => p.balance);
        if (zeroLine.HasValue)
        {
            minBal = Math.Min(minBal, zeroLine.Value);
            maxBal = Math.Max(maxBal, zeroLine.Value);
        }
        decimal range = maxBal - minBal;
        if (range == 0)
        {
            range = 1;
        }
        decimal pad = range * 0.1m;
        minBal -= pad;
        maxBal += pad;
        range = maxBal - minBal;

        double ScaleX(int i) => padL + (double)i / (points.Count - 1) * plotW;
        double ScaleY(decimal v) => padT + plotH - (double)((v - minBal) / range) * plotH;

        // Build polyline points
        var pts = string.Join(" ", points.Select((p, i) => $"{ScaleX(i):F1},{ScaleY(p.balance):F1}"));

        // Build area polygon (close below)
        double firstX = ScaleX(0), lastX = ScaleX(points.Count - 1);
        double bottom = padT + plotH;
        var areaPts = $"{firstX:F1},{bottom} {pts} {lastX:F1},{bottom}";

        // Gridlines (4 horizontal)
        var gridLines = new System.Text.StringBuilder();
        for (int g = 0; g <= 3; g++)
        {
            decimal gVal = minBal + range * g / 3m;
            double gy = ScaleY(gVal);
            string label = CompactMoney(gVal);
            gridLines.Append($"<line x1='{padL}' y1='{gy:F1}' x2='{W - padR}' y2='{gy:F1}' stroke='var(--mud-palette-lines-default)' stroke-width='1'/>");
            gridLines.Append($"<text x='{padL - 5}' y='{gy + 4:F1}' text-anchor='end' font-size='10' fill='var(--mud-palette-text-secondary)'>{label}</text>");
        }

        // X-axis ticks (3-4 evenly spaced)
        var xTicks = new System.Text.StringBuilder();
        int tickCount = Math.Min(4, points.Count);
        for (int t = 0; t < tickCount; t++)
        {
            int idx = (int)Math.Round((double)t / (tickCount - 1) * (points.Count - 1));
            double tx = ScaleX(idx);
            string label = Format.DayMonth(points[idx].date);
            xTicks.Append($"<text x='{tx:F1}' y='{H - 5}' text-anchor='middle' font-size='10' fill='var(--mud-palette-text-secondary)'>{label}</text>");
        }

        // Zero line
        string zeroLineStr = "";
        if (zeroLine.HasValue && zeroLine.Value >= minBal && zeroLine.Value <= maxBal)
        {
            double zy = ScaleY(zeroLine.Value);
            zeroLineStr = $"<line x1='{padL}' y1='{zy:F1}' x2='{W - padR}' y2='{zy:F1}' stroke='var(--mud-palette-error)' stroke-width='1' stroke-dasharray='4 3'/>";
        }

        // Breach markers
        var breachMarkers = new System.Text.StringBuilder();
        if (breaches != null)
        {
            var breachSet = new HashSet<DateOnly>(breaches);
            for (int i = 0; i < points.Count; i++)
            {
                if (breachSet.Contains(points[i].date))
                {
                    double bx = ScaleX(i), by = ScaleY(points[i].balance);
                    breachMarkers.Append($"<circle cx='{bx:F1}' cy='{by:F1}' r='4' fill='var(--mud-palette-error)' fill-opacity='0.7'/>");
                }
            }
        }

        // Low water mark
        string lwmStr = "";
        if (lowWaterMark.HasValue)
        {
            // Find closest point
            int lwmIdx = 0;
            for (int i = 1; i < points.Count; i++)
            {
                if (Math.Abs((points[i].date.DayNumber - lowWaterMark.Value.date.DayNumber)) < Math.Abs((points[lwmIdx].date.DayNumber - lowWaterMark.Value.date.DayNumber)))
                {
                    lwmIdx = i;
                }
            }
            double lx = ScaleX(lwmIdx), ly = ScaleY(lowWaterMark.Value.balance);
            lwmStr = $"<circle cx='{lx:F1}' cy='{ly:F1}' r='5' fill='var(--mud-palette-warning)' stroke='var(--mud-palette-surface)' stroke-width='2'/>";
        }

        // Last value dot + label
        double lastDotX = ScaleX(points.Count - 1), lastDotY = ScaleY(points[^1].balance);
        string lastLabel = CompactMoney(points[^1].balance);
        string lastDot = $"<circle cx='{lastDotX:F1}' cy='{lastDotY:F1}' r='4' fill='var(--mud-palette-primary)'/><text x='{lastDotX + 6:F1}' y='{lastDotY + 4:F1}' font-size='10' fill='var(--mud-palette-text-secondary)'>{lastLabel}</text>";

        // Estimate path length for stroke-dasharray
        double pathLen = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double dx = ScaleX(i) - ScaleX(i - 1);
            double dy = ScaleY(points[i].balance) - ScaleY(points[i - 1].balance);
            pathLen += Math.Sqrt(dx * dx + dy * dy);
        }
        int dashLen = (int)Math.Ceiling(pathLen) + 10;

        var svg = $@"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {H}' aria-hidden='true'>
  {gridLines}
  <polygon points='{areaPts}' fill='var(--mud-palette-primary)' fill-opacity='0.10'/>
  {zeroLineStr}
  <polyline class='fl-chart-line' style='--fl-dash:{dashLen}' points='{pts}' fill='none' stroke='var(--mud-palette-primary)' stroke-width='2'/>
  {breachMarkers}
  {lwmStr}
  {lastDot}
  {xTicks}
</svg>";
        return new MarkupString(svg);
    }

    private static string CompactMoney(decimal v)
    {
        string sign = v < 0 ? "-" : "";
        decimal abs = Math.Abs(v);
        if (abs >= 1_000_000m)
        {
            return $"{sign}${abs / 1_000_000m:F1}M";
        }
        if (abs >= 1_000m)
        {
            return $"{sign}${abs / 1_000m:F0}k";
        }
        return $"{sign}${abs:F0}";
    }
}
