using System.Diagnostics;
using System.Globalization;

namespace FlowLedger.Application.Features.Query;

/// <summary>A named, typed dataset the query language can read from.</summary>
public sealed class QueryTable(IReadOnlyList<string> columns, List<object?[]> rows)
{
    public IReadOnlyList<string> Columns { get; } = columns;
    public List<object?[]> Rows { get; } = rows;
}

public sealed record QueryColumn(string Name, string Type);

public sealed record QueryResult(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    long ElapsedMs);

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
public static class QueryEngine
{
    public const int MaxResultRows = 5000;

    public static QueryResult Execute(string query, IReadOnlyDictionary<string, QueryTable> datasets, DateOnly today)
    {
        var sw = Stopwatch.StartNew();
        var parsed = QueryParser.Parse(query);

        if (!datasets.TryGetValue(parsed.Source, out var source))
        {
            throw new QueryException(
                $"Unknown dataset '{parsed.Source}'. Available: {string.Join(", ", datasets.Keys.Order())}.", 0);
        }

        var ctx = new EvalContext(today);
        var names = source.Columns.ToList();
        var rows = source.Rows;

        foreach (var op in parsed.Operators)
        {
            (names, rows) = Apply(op, names, rows, ctx);
        }

        var truncated = rows.Count > MaxResultRows;
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
                return (names, rows.Where(r => ctx.Truthy(ctx.Eval(w.Predicate, scope, r))).ToList());

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

internal sealed class Scope
{
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names;

    public Scope(List<string> names)
    {
        _names = names;
        for (var i = 0; i < names.Count; i++)
        {
            _index.TryAdd(names[i], i);
        }
    }

    public int IndexOf(string name, int pos)
    {
        if (_index.TryGetValue(name, out var i))
        {
            return i;
        }

        throw new QueryException(
            $"Unknown column '{name}'. Available columns: {string.Join(", ", _names)}.", pos);
    }

    /// <summary>Static check so typos fail even when the table is empty.</summary>
    public void Check(Expr e)
    {
        switch (e)
        {
            case ColumnRef c: IndexOf(c.Name, c.Pos); break;
            case Unary u: Check(u.Operand); break;
            case Binary b: Check(b.Left); Check(b.Right); break;
            case InList l: Check(l.Value); foreach (var i in l.Items) { Check(i); } break;
            case Call c: foreach (var a in c.Args) { Check(a); } break;
        }
    }
}

internal static class Values
{
    public static int Compare(object? a, object? b)
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        if (a is decimal da && b is decimal db)
        {
            return da.CompareTo(db);
        }

        if (a is DateOnly xa && b is DateOnly xb)
        {
            return xa.CompareTo(xb);
        }

        if (a is bool ba && b is bool bb)
        {
            return ba.CompareTo(bb);
        }

        if (a is DateOnly dateA && b is string sb && DateOnly.TryParse(sb, CultureInfo.InvariantCulture, out var pb))
        {
            return dateA.CompareTo(pb);
        }

        if (b is DateOnly dateB && a is string sa && DateOnly.TryParse(sa, CultureInfo.InvariantCulture, out var pa))
        {
            return pa.CompareTo(dateB);
        }

        if (a is decimal na && b is string snb && decimal.TryParse(snb, NumberStyles.Number, CultureInfo.InvariantCulture, out var nb))
        {
            return na.CompareTo(nb);
        }

        if (b is decimal nb2 && a is string sna && decimal.TryParse(sna, NumberStyles.Number, CultureInfo.InvariantCulture, out var na2))
        {
            return na2.CompareTo(nb2);
        }

        return string.Compare(Str(a), Str(b), StringComparison.OrdinalIgnoreCase);
    }

    public static string Str(object? v) => v switch
    {
        null => "",
        string s => s,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}

internal sealed class EvalContext(DateOnly today)
{
    public bool Truthy(object? v) => v is true;

    public object? Eval(Expr e, Scope scope, object?[] row)
    {
        switch (e)
        {
            case Literal l: return l.Value;
            case DurationLiteral d: return new DurationValue(d.Amount, d.Unit);
            case ColumnRef c: return row[scope.IndexOf(c.Name, c.Pos)];
            case Unary u: return EvalUnary(u, scope, row);
            case Binary b: return EvalBinary(b, scope, row);
            case InList l:
            {
                var v = Eval(l.Value, scope, row);
                var found = l.Items.Any(i => Values.Compare(v, Eval(i, scope, row)) == 0 && v is not null);
                return l.Negated ? !found : found;
            }
            case Call c: return EvalCall(c, scope, row);
            default: throw new QueryException("Unsupported expression.");
        }
    }

    private object? EvalUnary(Unary u, Scope scope, object?[] row)
    {
        var v = Eval(u.Operand, scope, row);
        return u.Op switch
        {
            "not" => v is bool b ? !b : v is null ? null : throw new QueryException("'not' needs a true/false value."),
            "-" => v is decimal d ? -d : v is null ? null : throw new QueryException("Unary '-' needs a number."),
            _ => throw new QueryException($"Unsupported operator {u.Op}."),
        };
    }

    private object? EvalBinary(Binary b, Scope scope, object?[] row)
    {
        // Short-circuit logical operators.
        if (b.Op == "and")
        {
            return Truthy(Eval(b.Left, scope, row)) && Truthy(Eval(b.Right, scope, row));
        }

        if (b.Op == "or")
        {
            return Truthy(Eval(b.Left, scope, row)) || Truthy(Eval(b.Right, scope, row));
        }

        var l = Eval(b.Left, scope, row);
        var r = Eval(b.Right, scope, row);

        switch (b.Op)
        {
            case "==": return l is null || r is null ? l is null && r is null : Values.Compare(l, r) == 0;
            case "!=": return l is null || r is null ? !(l is null && r is null) : Values.Compare(l, r) != 0;
            case "<": return l is not null && r is not null && Values.Compare(l, r) < 0;
            case "<=": return l is not null && r is not null && Values.Compare(l, r) <= 0;
            case ">": return l is not null && r is not null && Values.Compare(l, r) > 0;
            case ">=": return l is not null && r is not null && Values.Compare(l, r) >= 0;
            case "contains": return l is not null && r is not null && Values.Str(l).Contains(Values.Str(r), StringComparison.OrdinalIgnoreCase);
            case "startswith": return l is not null && r is not null && Values.Str(l).StartsWith(Values.Str(r), StringComparison.OrdinalIgnoreCase);
            case "endswith": return l is not null && r is not null && Values.Str(l).EndsWith(Values.Str(r), StringComparison.OrdinalIgnoreCase);
        }

        return Arithmetic(b.Op, l, r);
    }

    private static object? Arithmetic(string op, object? l, object? r)
    {
        if (l is null || r is null)
        {
            return null;
        }

        if (l is DateOnly d)
        {
            if (r is DurationValue dur)
            {
                return op == "-" ? dur.SubtractFrom(d) : op == "+" ? dur.AddTo(d) : Bad(op);
            }

            if (r is decimal days)
            {
                return op == "-" ? d.AddDays(-(int)days) : op == "+" ? d.AddDays((int)days) : Bad(op);
            }

            if (r is DateOnly d2 && op == "-")
            {
                return (decimal)(d.DayNumber - d2.DayNumber);
            }
        }

        if (op == "+" && (l is string || r is string))
        {
            return Values.Str(l) + Values.Str(r);
        }

        if (l is decimal x && r is decimal y)
        {
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y == 0 ? null : x / y,
                "%" => y == 0 ? null : x % y,
                _ => Bad(op),
            };
        }

        throw new QueryException($"Can't apply '{op}' to {TypeName(l)} and {TypeName(r)}.");
    }

    private static object? Bad(string op) => throw new QueryException($"Operator '{op}' isn't valid here.");

    private static string TypeName(object? v) => v switch
    {
        decimal => "number", string => "string", DateOnly => "date", bool => "bool", DurationValue => "duration", _ => "value",
    };

    private object? EvalCall(Call c, Scope scope, object?[] row)
    {
        object? Arg(int i) => Eval(c.Args[i], scope, row);
        void Arity(int min, int max)
        {
            if (c.Args.Count < min || c.Args.Count > max)
            {
                throw new QueryException($"{c.Name}() takes {(min == max ? min.ToString() : $"{min}-{max}")} argument(s).", c.Pos);
            }
        }

        DateOnly? D(int i)
        {
            var v = Arg(i);
            return v switch
            {
                null => null,
                DateOnly d => d,
                string s when DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var p) => p,
                _ => throw new QueryException($"{c.Name}() needs a date.", c.Pos),
            };
        }

        decimal? N(int i)
        {
            var v = Arg(i);
            return v switch
            {
                null => null,
                decimal d => d,
                string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
                _ => throw new QueryException($"{c.Name}() needs a number.", c.Pos),
            };
        }

        switch (c.Name)
        {
            case "today":
            case "now":
                Arity(0, 0);
                return today;
            case "ago":
            {
                Arity(1, 1);
                return Arg(0) switch
                {
                    DurationValue dur => dur.SubtractFrom(today),
                    decimal days => today.AddDays(-(int)days),
                    _ => throw new QueryException("ago() takes a duration such as 30d, 2w, 6mo or 1y.", c.Pos),
                };
            }
            case "year": Arity(1, 1); return D(0) is { } y ? (decimal)y.Year : null;
            case "month": Arity(1, 1); return D(0) is { } m ? m.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null;
            case "quarter": Arity(1, 1); return D(0) is { } q ? $"{q.Year}-Q{(q.Month - 1) / 3 + 1}" : null;
            case "week":
            case "startofweek":
                Arity(1, 1);
                return D(0) is { } w ? w.AddDays(-(((int)w.DayOfWeek + 6) % 7)) : null; // Monday
            case "startofmonth": Arity(1, 1); return D(0) is { } sm ? new DateOnly(sm.Year, sm.Month, 1) : null;
            case "startofyear": Arity(1, 1); return D(0) is { } sy ? new DateOnly(sy.Year, 1, 1) : null;
            case "dayofweek": Arity(1, 1); return D(0) is { } dw ? dw.DayOfWeek.ToString() : null;
            case "dayofmonth": Arity(1, 1); return D(0) is { } dm ? (decimal)dm.Day : null;
            case "abs": Arity(1, 1); return N(0) is { } a ? Math.Abs(a) : null;
            case "floor": Arity(1, 1); return N(0) is { } f ? Math.Floor(f) : null;
            case "ceil": Arity(1, 1); return N(0) is { } ce ? Math.Ceiling(ce) : null;
            case "sign": Arity(1, 1); return N(0) is { } sg ? (decimal)Math.Sign(sg) : null;
            case "round":
            {
                Arity(1, 2);
                var digits = c.Args.Count > 1 ? (int)(N(1) ?? 0) : 0;
                return N(0) is { } r ? Math.Round(r, Math.Clamp(digits, 0, 8), MidpointRounding.AwayFromZero) : null;
            }
            case "lower": Arity(1, 1); return Arg(0) is { } lo ? Values.Str(lo).ToLowerInvariant() : null;
            case "upper": Arity(1, 1); return Arg(0) is { } up ? Values.Str(up).ToUpperInvariant() : null;
            case "trim": Arity(1, 1); return Arg(0) is { } tr ? Values.Str(tr).Trim() : null;
            case "len": Arity(1, 1); return Arg(0) is { } ln ? (decimal)Values.Str(ln).Length : null;
            case "tostring": Arity(1, 1); return Arg(0) is { } ts ? Values.Str(ts) : null;
            case "tonumber": Arity(1, 1); return N(0);
            case "strcat": return string.Concat(Enumerable.Range(0, c.Args.Count).Select(i => Values.Str(Arg(i))));
            case "isnull": Arity(1, 1); return Arg(0) is null;
            case "isnotnull": Arity(1, 1); return Arg(0) is not null;
            case "coalesce":
                for (var i = 0; i < c.Args.Count; i++)
                {
                    var v = Arg(i);
                    if (v is not null && v is not "")
                    {
                        return v;
                    }
                }
                return null;
            case "iif":
            case "iff":
                Arity(3, 3);
                return Truthy(Arg(0)) ? Arg(1) : Arg(2);
            case "datediff":
            {
                Arity(2, 2);
                return D(0) is { } d1 && D(1) is { } d2 ? (decimal)(d1.DayNumber - d2.DayNumber) : null;
            }
            default:
                if (IsAggregateName(c.Name))
                {
                    throw new QueryException($"{c.Name}() is an aggregate; use it inside 'summarize'.", c.Pos);
                }

                throw new QueryException($"Unknown function '{c.Name}'.", c.Pos);
        }
    }

    private static bool IsAggregateName(string n)
        => n is "count" or "sum" or "avg" or "min" or "max" or "dcount" or "countif" or "sumif";

    // ── Aggregates ─────────────────────────────────────────────────────────────

    public object? Aggregate(Aggregate a, Scope scope, List<object?[]> rows)
    {
        switch (a.Function)
        {
            case "count":
                return (decimal)rows.Count;
            case "countif":
                return (decimal)rows.Count(r => Truthy(Eval(a.Args[0], scope, r)));
            case "sumif":
                return Sum(rows.Where(r => Truthy(Eval(a.Args[1], scope, r))).Select(r => Eval(a.Args[0], scope, r)));
        }

        var values = rows.Select(r => Eval(a.Args[0], scope, r)).Where(v => v is not null).ToList();
        switch (a.Function)
        {
            case "sum": return Sum(values);
            case "avg":
            {
                var nums = Numbers(values, a);
                return nums.Count == 0 ? null : nums.Sum() / nums.Count;
            }
            case "min": return values.Count == 0 ? null : values.MinBy(v => v, Comparer<object?>.Create(Values.Compare));
            case "max": return values.Count == 0 ? null : values.MaxBy(v => v, Comparer<object?>.Create(Values.Compare));
            case "dcount": return (decimal)values.Select(v => Values.Str(v).ToLowerInvariant()).Distinct().Count();
            default: throw new QueryException($"Unknown aggregate '{a.Function}'.", a.Pos);
        }
    }

    private static decimal Sum(IEnumerable<object?> values)
    {
        decimal total = 0;
        foreach (var v in values)
        {
            if (v is null)
            {
                continue;
            }

            if (v is decimal d)
            {
                total += d;
            }
            else
            {
                throw new QueryException("sum() needs numeric values.");
            }
        }
        return total;
    }

    private static List<decimal> Numbers(List<object?> values, Aggregate a)
    {
        var list = new List<decimal>(values.Count);
        foreach (var v in values)
        {
            if (v is decimal d)
            {
                list.Add(d);
            }
            else
            {
                throw new QueryException($"{a.Function}() needs numeric values.", a.Pos);
            }
        }
        return list;
    }
}
