using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace FlowLedger.Web.Shared;

/// <summary>One plotted series; <c>Values[i]</c> aligns with <c>ChartModel.Categories[i]</c> (null = no data).</summary>
public sealed record ChartSeries(string Name, double?[] Values);

/// <summary>Chart-ready shape derived from a tabular query result and a <see cref="VizSpec"/>.</summary>
public sealed record ChartModel(IReadOnlyList<string> Categories, IReadOnlyList<ChartSeries> Series, bool XIsTime, string? Problem)
{
    public const int MaxSeries = 8;

    /// <summary>Result cells arrive as <see cref="JsonElement"/> boxes (the shared contract types them as object); anything else is normalised first.</summary>
    public static JsonElement ToElement(object? cell) => cell switch
    {
        JsonElement e => e,
        null => default,
        _ => JsonSerializer.SerializeToElement(cell),
    };

    public static string CellText(object? cell) => CellText(ToElement(cell));

    public static string CellText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetDecimal().ToString("0.####", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.ToString(),
    };

    public static double? CellNumber(object? cell) => CellNumber(ToElement(cell));

    public static double? CellNumber(JsonElement e)
        => e.ValueKind == JsonValueKind.Number ? (double)e.GetDecimal() : null;

    public static ChartModel Build(QueryResult result, VizSpec viz)
    {
        if (result.Rows.Count == 0)
        {
            return new ChartModel([], [], false, "The query returned no rows.");
        }

        var cols = result.Columns.Select(c => c.Name).ToList();
        int Idx(string? name) => name is null ? -1 : cols.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

        var xi = Idx(viz.X);
        if (xi < 0)
        {
            xi = 0;
        }

        var seriesByIdx = Idx(viz.SeriesBy);
        var yIdx = (viz.Y ?? []).Select(Idx).Where(i => i >= 0 && i != xi).ToList();
        if (yIdx.Count == 0)
        {
            yIdx = result.Columns.Select((c, i) => (c, i)).Where(t => t.c.Type == "number" && t.i != xi && t.i != seriesByIdx)
                .Select(t => t.i).ToList();
        }

        if (yIdx.Count == 0)
        {
            return new ChartModel([], [], false, "Charts need at least one numeric column besides the X axis.");
        }

        var xIsTime = result.Columns[xi].Type == "date" || (ToElement(result.Rows[0][xi]) is { ValueKind: JsonValueKind.String } first
            && System.Text.RegularExpressions.Regex.IsMatch(first.GetString() ?? "", @"^\d{4}-(\d{2}|Q\d)"));

        var categories = new List<string>();
        var catIndex = new Dictionary<string, int>();
        foreach (var row in result.Rows)
        {
            var key = CellText(row[xi]);
            if (!catIndex.ContainsKey(key))
            {
                catIndex[key] = categories.Count;
                categories.Add(key);
            }
        }

        var series = new List<ChartSeries>();
        if (seriesByIdx >= 0)
        {
            var y = yIdx[0];
            var map = new Dictionary<string, double?[]>();
            foreach (var row in result.Rows)
            {
                var name = CellText(row[seriesByIdx]);
                if (!map.TryGetValue(name, out var values))
                {
                    values = new double?[categories.Count];
                    map[name] = values;
                }

                var c = catIndex[CellText(row[xi])];
                values[c] = (values[c] ?? 0) + (CellNumber(row[y]) ?? 0);
            }

            series.AddRange(map.Select(kv => new ChartSeries(string.IsNullOrEmpty(kv.Key) ? "(blank)" : kv.Key, kv.Value)));
        }
        else
        {
            foreach (var y in yIdx)
            {
                var values = new double?[categories.Count];
                foreach (var row in result.Rows)
                {
                    var c = catIndex[CellText(row[xi])];
                    var n = CellNumber(row[y]);
                    values[c] = n is null ? values[c] : (values[c] ?? 0) + n;
                }

                series.Add(new ChartSeries(cols[y], values));
            }
        }

        return new ChartModel(categories, FoldExtra(series, categories.Count), xIsTime, null);
    }

    /// <summary>A 9th hue is never invented: surplus series fold into "Other".</summary>
    private static List<ChartSeries> FoldExtra(List<ChartSeries> series, int width)
    {
        if (series.Count <= MaxSeries)
        {
            return series;
        }

        var ranked = series.OrderByDescending(s => s.Values.Sum(v => Math.Abs(v ?? 0))).ToList();
        var keep = ranked.Take(MaxSeries - 1).ToHashSet();
        var other = new double?[width];
        foreach (var s in ranked.Skip(MaxSeries - 1))
        {
            for (var i = 0; i < width; i++)
            {
                if (s.Values[i] is { } v)
                {
                    other[i] = (other[i] ?? 0) + v;
                }
            }
        }

        return series.Where(keep.Contains).Append(new ChartSeries("Other", other)).ToList();
    }

    public static string Compact(double v)
    {
        var a = Math.Abs(v);
        var sign = v < 0 ? "-" : "";
        return a >= 1_000_000 ? $"{sign}{a / 1_000_000:0.#}M"
            : a >= 1_000 ? $"{sign}{a / 1_000:0.#}k"
            : a >= 10 ? $"{sign}{a:0}" : $"{sign}{a:0.##}";
    }
}

/// <summary>Renders a <see cref="ChartModel"/> as inline SVG that follows the page theme through CSS variables.</summary>
public static class QueryChartSvg
{
    private const int W = 760, H = 300, PadL = 56, PadR = 34, PadT = 14;

    public static string SeriesVar(int i) => $"var(--fl-series-{i % ChartModel.MaxSeries})";

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    public static MarkupString Render(ChartModel m, string kind, bool stacked)
    {
        if (m.Problem is not null || m.Series.Count == 0)
        {
            return new MarkupString("");
        }

        return new MarkupString(kind switch
        {
            "pie" => Pie(m),
            "bar" => Bars(m, horizontal: true, stacked),
            "column" => Bars(m, horizontal: false, stacked),
            "area" => Lines(m, area: true, stacked),
            _ => Lines(m, area: false, stacked: false),
        });
    }

    private static (double min, double max) Range(ChartModel m, bool stacked, bool includeZero)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < m.Categories.Count; i++)
        {
            if (stacked)
            {
                var pos = m.Series.Sum(s => Math.Max(0, s.Values[i] ?? 0));
                var neg = m.Series.Sum(s => Math.Min(0, s.Values[i] ?? 0));
                min = Math.Min(min, neg);
                max = Math.Max(max, pos);
            }
            else
            {
                foreach (var s in m.Series)
                {
                    if (s.Values[i] is { } v)
                    {
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }
            }
        }

        if (min == double.MaxValue)
        {
            return (0, 1);
        }

        if (includeZero)
        {
            min = Math.Min(min, 0);
            max = Math.Max(max, 0);
        }

        if (Math.Abs(max - min) < 1e-9)
        {
            max = min + 1;
        }

        // Nice-ish padding so the top gridline is not clipped.
        var pad = (max - min) * 0.06;
        return (min < 0 ? min - pad : min, max + pad);
    }

    private static void YGrid(StringBuilder sb, double min, double max, Func<double, double> y, int right)
    {
        for (var g = 0; g <= 4; g++)
        {
            var v = min + (max - min) * g / 4;
            var gy = y(v);
            sb.Append(CultureInfo.InvariantCulture, $"<line x1='{PadL}' y1='{gy:F1}' x2='{right}' y2='{gy:F1}' stroke='var(--fl-grid)' stroke-width='1'/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x='{PadL - 6}' y='{gy + 4:F1}' text-anchor='end' font-size='11' fill='var(--fl-muted)'>{Esc(ChartModel.Compact(v))}</text>");
        }
    }

    private static string Svg(string body, string label)
        => $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {H}' role='img' aria-label='{Esc(label)}' class='fl-viz-svg'>{body}</svg>";

    private static string Lines(ChartModel m, bool area, bool stacked)
    {
        const int padB = 38;
        var n = m.Categories.Count;
        var (min, max) = Range(m, stacked && area, includeZero: area);
        var plotW = W - PadL - PadR;
        var plotH = H - PadT - padB;
        double X(int i) => n == 1 ? PadL + plotW / 2.0 : PadL + (double)i / (n - 1) * plotW;
        double Y(double v) => PadT + plotH - (v - min) / (max - min) * plotH;

        var sb = new StringBuilder();
        YGrid(sb, min, max, Y, W - PadR);
        XLabels(sb, m.Categories, X, H - 10, Math.Max(1, (int)Math.Ceiling(n / 8.0)));

        var cum = new double[n];
        for (var si = 0; si < m.Series.Count; si++)
        {
            var s = m.Series[si];
            var color = SeriesVar(si);
            var tops = new double?[n];
            var bases = new double[n];
            for (var i = 0; i < n; i++)
            {
                if (s.Values[i] is { } v)
                {
                    bases[i] = stacked && area ? cum[i] : 0;
                    tops[i] = (stacked && area ? cum[i] : 0) + v;
                    if (stacked && area)
                    {
                        cum[i] += v;
                    }
                }
            }

            // Break the line at gaps instead of drawing through missing data.
            var runs = new List<List<int>>();
            List<int>? run = null;
            for (var i = 0; i < n; i++)
            {
                if (tops[i] is null)
                {
                    run = null;
                    continue;
                }

                if (run is null)
                {
                    run = [];
                    runs.Add(run);
                }

                run.Add(i);
            }

            foreach (var r in runs)
            {
                var pts = string.Join(" ", r.Select(i => $"{X(i):F1},{Y(tops[i]!.Value):F1}"));
                if (area && r.Count > 1)
                {
                    var back = string.Join(" ", Enumerable.Reverse(r).Select(i => $"{X(i):F1},{Y(bases[i]):F1}"));
                    sb.Append(CultureInfo.InvariantCulture, $"<polygon points='{pts} {back}' fill='{color}' fill-opacity='0.28' stroke='var(--fl-surface)' stroke-width='1'/>");
                }

                if (r.Count > 1)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<polyline points='{pts}' fill='none' stroke='{color}' stroke-width='2' stroke-linejoin='round' stroke-linecap='round'/>");
                }
            }

            for (var i = 0; i < n; i++)
            {
                if (tops[i] is { } t)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<circle cx='{X(i):F1}' cy='{Y(t):F1}' r='{(n > 40 ? 2 : 3.5)}' fill='{color}' stroke='var(--fl-surface)' stroke-width='2'>" +
                              $"<title>{Esc(s.Name)} · {Esc(m.Categories[i])}: {Esc(s.Values[i]!.Value.ToString("N2", CultureInfo.InvariantCulture))}</title></circle>");
                }
            }
        }

        return Svg(sb.ToString(), $"Line chart of {string.Join(", ", m.Series.Select(s => s.Name))} by {m.Categories.Count} points");
    }

    private static void XLabels(StringBuilder sb, IReadOnlyList<string> cats, Func<int, double> x, int y, int step)
    {
        for (var i = 0; i < cats.Count; i += step)
        {
            var label = cats[i].Length > 12 ? cats[i][..11] + "…" : cats[i];
            sb.Append(CultureInfo.InvariantCulture, $"<text x='{x(i):F1}' y='{y}' text-anchor='middle' font-size='11' fill='var(--fl-muted)'>{Esc(label)}</text>");
        }
    }

    private static string Bars(ChartModel m, bool horizontal, bool stacked)
    {
        var n = m.Categories.Count;
        var (min, max) = Range(m, stacked, includeZero: true);
        var sb = new StringBuilder();
        var sc = m.Series.Count;

        if (horizontal)
        {
            const int labelW = 130;
            var rowH = Math.Clamp(280.0 / Math.Max(n, 1), 14, 40);
            var h = (int)(n * rowH + 40);
            var plotW = W - labelW - PadR - 40;
            double X(double v) => labelW + (v - min) / (max - min) * plotW;
            var zero = X(0);
            for (var g = 0; g <= 4; g++)
            {
                var v = min + (max - min) * g / 4;
                sb.Append(CultureInfo.InvariantCulture, $"<line x1='{X(v):F1}' y1='8' x2='{X(v):F1}' y2='{h - 24}' stroke='var(--fl-grid)'/>");
                sb.Append(CultureInfo.InvariantCulture, $"<text x='{X(v):F1}' y='{h - 8}' text-anchor='middle' font-size='11' fill='var(--fl-muted)'>{Esc(ChartModel.Compact(v))}</text>");
            }

            for (var i = 0; i < n; i++)
            {
                var top = 8 + i * rowH;
                var label = m.Categories[i].Length > 17 ? m.Categories[i][..16] + "…" : m.Categories[i];
                sb.Append(CultureInfo.InvariantCulture, $"<text x='{labelW - 8}' y='{top + rowH / 2 + 4:F1}' text-anchor='end' font-size='12' fill='var(--fl-text)'>{Esc(label)}</text>");
                double pos = 0, neg = 0;
                var barH = Math.Max(4, (rowH - 4) / (stacked ? 1 : sc) - 2);
                for (var si = 0; si < sc; si++)
                {
                    if (m.Series[si].Values[i] is not { } v)
                    {
                        continue;
                    }

                    double a, b;
                    if (stacked) { (a, b) = v >= 0 ? (pos, pos += v) : (neg, neg += v); }
                    else { (a, b) = (0, v); }
                    var y = stacked ? top + 2 : top + 2 + si * (barH + 2);
                    var x0 = X(Math.Min(a, b));
                    var w = Math.Max(1, Math.Abs(X(b) - X(a)) - (stacked ? 2 : 0));
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x='{x0:F1}' y='{y:F1}' width='{w:F1}' height='{barH:F1}' rx='3' fill='{SeriesVar(si)}'>" +
                              $"<title>{Esc(m.Series[si].Name)} · {Esc(m.Categories[i])}: {v:N2}</title></rect>");
                }
            }

            sb.Append(CultureInfo.InvariantCulture, $"<line x1='{zero:F1}' y1='8' x2='{zero:F1}' y2='{h - 24}' stroke='var(--fl-axis)'/>");
            return $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {h}' role='img' aria-label='Bar chart' class='fl-viz-svg'>{sb}</svg>";
        }

        const int padB = 38;
        var plotWc = W - PadL - PadR;
        var plotH = H - PadT - padB;
        double Y(double v) => PadT + plotH - (v - min) / (max - min) * plotH;
        YGrid(sb, min, max, Y, W - PadR);
        var slot = (double)plotWc / n;
        var groupW = slot * 0.72;
        var barW = Math.Max(3, (stacked ? groupW : groupW / sc) - 2);
        for (var i = 0; i < n; i++)
        {
            var left = PadL + i * slot + (slot - groupW) / 2;
            double pos = 0, neg = 0;
            for (var si = 0; si < sc; si++)
            {
                if (m.Series[si].Values[i] is not { } v)
                {
                    continue;
                }

                double a, b;
                if (stacked) { (a, b) = v >= 0 ? (pos, pos += v) : (neg, neg += v); }
                else { (a, b) = (0, v); }
                var x = stacked ? left : left + si * (barW + 2);
                var yTop = Y(Math.Max(a, b));
                var hgt = Math.Max(1, Math.Abs(Y(a) - Y(b)) - (stacked ? 2 : 0));
                sb.Append(CultureInfo.InvariantCulture, $"<rect x='{x:F1}' y='{yTop:F1}' width='{barW:F1}' height='{hgt:F1}' rx='3' fill='{SeriesVar(si)}'>" +
                          $"<title>{Esc(m.Series[si].Name)} · {Esc(m.Categories[i])}: {v:N2}</title></rect>");
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $"<line x1='{PadL}' y1='{Y(0):F1}' x2='{W - PadR}' y2='{Y(0):F1}' stroke='var(--fl-axis)'/>");
        XLabels(sb, m.Categories, i => PadL + i * slot + slot / 2, H - 10, Math.Max(1, (int)Math.Ceiling(n / 10.0)));
        return Svg(sb.ToString(), "Column chart");
    }

    private static string Pie(ChartModel m)
    {
        var values = m.Series[0].Values.Select(v => Math.Max(0, v ?? 0)).ToList();
        var total = values.Sum();
        if (total <= 0)
        {
            return "";
        }

        // Fold the tail so a pie never needs more than the eight validated hues.
        var items = m.Categories.Select((c, i) => (Name: c, Value: values[i])).Where(t => t.Value > 0)
            .OrderByDescending(t => t.Value).ToList();
        if (items.Count > ChartModel.MaxSeries)
        {
            var tail = items.Skip(ChartModel.MaxSeries - 1).Sum(t => t.Value);
            items = items.Take(ChartModel.MaxSeries - 1).Append(("Other", tail)).ToList();
        }

        const double cx = 150, cy = 150, r = 130, inner = 78;
        var sb = new StringBuilder();
        var angle = -Math.PI / 2;
        for (var i = 0; i < items.Count; i++)
        {
            var frac = items[i].Value / total;
            var sweep = frac * 2 * Math.PI;
            var end = angle + Math.Min(sweep, 2 * Math.PI - 1e-4);
            var large = sweep > Math.PI ? 1 : 0;
            string P(double rad, double ang) => $"{cx + rad * Math.Cos(ang):F2},{cy + rad * Math.Sin(ang):F2}";
            sb.Append(CultureInfo.InvariantCulture, $"<path d='M {P(r, angle)} A {r} {r} 0 {large} 1 {P(r, end)} L {P(inner, end)} A {inner} {inner} 0 {large} 0 {P(inner, angle)} Z' " +
                      $"fill='{SeriesVar(i)}' stroke='var(--fl-surface)' stroke-width='2'><title>{Esc(items[i].Name)}: {items[i].Value:N2} ({frac:P1})</title></path>");
            angle += sweep;
        }

        sb.Append(CultureInfo.InvariantCulture, $"<text x='{cx}' y='{cy - 2}' text-anchor='middle' font-size='20' font-weight='600' fill='var(--fl-text)'>{Esc(ChartModel.Compact(total))}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x='{cx}' y='{cy + 16}' text-anchor='middle' font-size='11' fill='var(--fl-muted)'>total</text>");

        // Legend lives inside the SVG so slices and names stay together at any width.
        for (var i = 0; i < items.Count; i++)
        {
            var y = 36 + i * 26;
            var name = items[i].Name.Length > 22 ? items[i].Name[..21] + "…" : items[i].Name;
            sb.Append(CultureInfo.InvariantCulture, $"<rect x='330' y='{y - 10}' width='12' height='12' rx='3' fill='{SeriesVar(i)}'/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x='350' y='{y}' font-size='12' fill='var(--fl-text)'>{Esc(name)}</text>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x='640' y='{y}' text-anchor='end' font-size='12' fill='var(--fl-muted)'>{items[i].Value / total:P0}</text>");
        }

        return $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} 300' role='img' aria-label='Donut chart' class='fl-viz-svg'>{sb}</svg>";
    }
}
