using System.Diagnostics;
using System.Globalization;

namespace FlowLedger.Application.Features.Query;

/// <summary>A named, typed dataset the query language can read from.</summary>
public sealed class QueryTable(IReadOnlyList<string> columns, List<object?[]> rows)
{
    public IReadOnlyList<string> Columns { get; } = columns;
    public List<object?[]> Rows { get; } = rows;

    /// <summary>True when the dataset itself was capped (more data exists than was loaded).</summary>
    public bool InputTruncated { get; init; }
}

internal readonly record struct DurationValue(int Amount, string Unit)
{
    public DateOnly SubtractFrom(DateOnly d) => Unit switch
    {
        "d" => d.AddDays(-Amount),
        "w" => d.AddDays(-7 * Amount),
        "mo" => d.AddMonths(-Amount),
        _ => d.AddYears(-Amount),
    };

    public DateOnly AddTo(DateOnly d) => Unit switch
    {
        "d" => d.AddDays(Amount),
        "w" => d.AddDays(7 * Amount),
        "mo" => d.AddMonths(Amount),
        _ => d.AddYears(Amount),
    };
}

/// <summary>
/// In-memory, pipeline-style query engine (Kusto/Splunk flavoured):
/// <c>dataset | where … | summarize … by … | sort by … | take N</c>.
/// Everything is evaluated against already tenant-scoped datasets in memory, so there is no
/// SQL surface and no way to reach another tenant's data.
/// </summary>
internal static class QueryEngine
{
    public const int MaxResultRows = 5000;

    public static QueryResult Execute(string query, IReadOnlyDictionary<string, QueryTable> datasets, DateOnly today, QueryBudget? budget = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var parsed = QueryParser.Parse(query);

        if (!datasets.TryGetValue(parsed.Source, out var source))
        {
            throw new QueryException(
                $"Unknown dataset '{parsed.Source}'. Available: {string.Join(", ", datasets.Keys.Order())}.", 0);
        }

        var ctx = new EvalContext(today, budget ?? QueryBudget.Default, sw, ct);
        var names = source.Columns.ToList();
        var rows = source.Rows;

        try
        {
            foreach (var op in parsed.Operators)
            {
                (names, rows) = ctx.Guard(Apply(op, names, rows, ctx));
            }
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or ArgumentException or FormatException or InvalidCastException)
        {
            // Hostile or sloppy input (ago(99999999d), huge multiplications…) is a user error, not a server fault.
            throw new QueryException("A value in the query is out of range or has the wrong type.");
        }

        var truncated = source.InputTruncated || rows.Count > MaxResultRows;
        if (truncated)
        {
            rows = rows.Take(MaxResultRows).ToList();
        }

        var columns = names.Select((n, i) => new QueryColumn(n, InferType(rows, i))).ToList();
        var outRows = rows.Select(r => (IReadOnlyList<object?>)r.Select(Normalize).ToArray()).ToList();
        return new QueryResult(columns, outRows, truncated, sw.ElapsedMilliseconds);
    }

    /// <summary>Column names of a query's output without caring about rows (used for validation).</summary>
    public static IReadOnlyList<string> Describe(string query, IReadOnlyDictionary<string, QueryTable> datasets, DateOnly today)
        => Execute(query, datasets, today).Columns.Select(c => c.Name).ToList();

    private static object? Normalize(object? v) => v switch
    {
        decimal d => Math.Round(d, 4),
        _ => v,
    };

    private static string InferType(List<object?[]> rows, int col)
    {
        foreach (var r in rows)
        {
            switch (r[col])
            {
                case null: continue;
                case decimal: return "number";
                case DateOnly: return "date";
                case bool: return "bool";
                default: return "string";
            }
        }
        return "string";
    }

    // ── Operators ──────────────────────────────────────────────────────────────

    private static (List<string>, List<object?[]>) Apply(Operator op, List<string> names, List<object?[]> rows, EvalContext ctx)
    {
        var scope = new Scope(names);
        switch (op)
        {
            case WhereOp w:
                scope.Check(w.Predicate);
                return (names, rows.Where(r => EvalContext.Truthy(ctx.Eval(w.Predicate, scope, r))).ToList());

            case ProjectOp p:
                foreach (var i in p.Items)
                {
                    scope.Check(i.Expr);
                }

                return (p.Items.Select(i => i.Name).ToList(),
                    rows.Select(r => p.Items.Select(i => ctx.Eval(i.Expr, scope, r)).ToArray()).ToList());

            case ExtendOp e:
                {
                    var newNames = names.ToList();
                    var work = rows.Select(r => r.ToList()).ToList();
                    foreach (var item in e.Items)
                    {
                        var s = new Scope(newNames);
                        s.Check(item.Expr);
                        var idx = newNames.FindIndex(n => n.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                        foreach (var r in work)
                        {
                            var v = ctx.Eval(item.Expr, s, r.ToArray());
                            if (idx >= 0)
                            {
                                r[idx] = v;
                            }
                            else
                            {
                                r.Add(v);
                            }
                        }
                        if (idx < 0)
                        {
                            newNames.Add(item.Name);
                        }
                    }
                    return (newNames, work.Select(r => r.ToArray()).ToList());
                }

            case SummarizeOp s:
                return Summarize(s, names, rows, ctx);

            case SortOp so:
                return (names, Sort(rows, so.Keys, scope, ctx));

            case TakeOp t:
                return (names, rows.Take(t.Count).ToList());

            case TopOp top:
                return (names, Sort(rows, [top.Key], scope, ctx).Take(top.Count).ToList());

            case DistinctOp d:
                {
                    foreach (var i in d.Items)
                    {
                        scope.Check(i.Expr);
                    }

                    var seen = new HashSet<string>();
                    var outRows = new List<object?[]>();
                    foreach (var r in rows)
                    {
                        var vals = d.Items.Select(i => ctx.Eval(i.Expr, scope, r)).ToArray();
                        if (seen.Add(KeyOf(vals)))
                        {
                            outRows.Add(vals);
                        }
                    }
                    return (d.Items.Select(i => i.Name).ToList(), outRows);
                }

            case CountOp:
                return (["count"], [[(decimal)rows.Count]]);

            default:
                throw new QueryException("Unsupported operator.");
        }
    }

    private static List<object?[]> Sort(List<object?[]> rows, IReadOnlyList<SortKey> keys, Scope scope, EvalContext ctx)
    {
        foreach (var k in keys)
        {
            scope.Check(k.Expr);
        }

        var keyed = rows.Select(r => (Row: r, Keys: keys.Select(k => ctx.Eval(k.Expr, scope, r)).ToArray())).ToList();
        // OrderBy is stable, so ties keep input order.
        IOrderedEnumerable<(object?[] Row, object?[] Keys)>? ordered = null;
        for (var i = 0; i < keys.Count; i++)
        {
            var idx = i;
            var cmp = Comparer<object?>.Create(Values.Compare);
            ordered = ordered is null
                ? (keys[i].Descending ? keyed.OrderByDescending(x => x.Keys[idx], cmp) : keyed.OrderBy(x => x.Keys[idx], cmp))
                : (keys[i].Descending ? ordered.ThenByDescending(x => x.Keys[idx], cmp) : ordered.ThenBy(x => x.Keys[idx], cmp));
        }
        return ordered!.Select(x => x.Row).ToList();
    }

    private static (List<string>, List<object?[]>) Summarize(SummarizeOp s, List<string> names, List<object?[]> rows, EvalContext ctx)
    {
        var scope = new Scope(names);
        foreach (var b in s.By)
        {
            scope.Check(b.Expr);
        }

        foreach (var a in s.Aggregates)
        {
            foreach (var arg in a.Args)
            {
                scope.Check(arg);
            }
        }

        var groups = new Dictionary<string, (object?[] Key, List<object?[]> Rows)>();
        var order = new List<string>();
        foreach (var r in rows)
        {
            var key = s.By.Select(b => ctx.Eval(b.Expr, scope, r)).ToArray();
            var k = KeyOf(key);
            if (!groups.TryGetValue(k, out var g))
            {
                g = (key, []);
                groups[k] = g;
                order.Add(k);
            }
            g.Rows.Add(r);
        }

        // Aggregating with no rows and no grouping still yields one row (count = 0), like SQL.
        if (s.By.Count == 0 && groups.Count == 0)
        {
            groups[""] = ([], []);
            order.Add("");
        }

        var outNames = s.By.Select(b => b.Name).Concat(s.Aggregates.Select(a => a.Name)).ToList();
        var outRows = order.Select(k =>
        {
            var (key, grpRows) = groups[k];
            return key.Concat(s.Aggregates.Select(a => ctx.Aggregate(a, scope, grpRows))).ToArray();
        }).ToList();

        if (s.By.Count > 0)
        {
            // Deterministic output (and chronological trends): order by the grouping keys ascending.
            outRows = outRows.OrderBy(r => r, Comparer<object?[]>.Create((x, y) =>
            {
                for (var i = 0; i < s.By.Count; i++)
                {
                    var c = Values.Compare(x[i], y[i]);
                    if (c != 0)
                    {
                        return c;
                    }
                }
                return 0;
            })).ToList();
        }

        return (outNames, outRows);
    }

    private static string KeyOf(object?[] vals)
        => string.Join('\u0001', vals.Select(v => v switch
        {
            null => "\u0000null",
            string str => "s:" + str.ToLowerInvariant(),
            _ => v.GetType().Name + ":" + Convert.ToString(v, CultureInfo.InvariantCulture),
        }));
}
