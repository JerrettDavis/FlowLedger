using System.Diagnostics;
using System.Globalization;

namespace FlowLedger.Application.Features.Query;

// Expression evaluation and aggregates (#103: split out of QueryEngine.cs).
internal sealed partial class EvalContext(DateOnly today, QueryBudget budget, Stopwatch clock, CancellationToken ct)
{
    public object? Eval(Expr e, Scope scope, object?[] row)
    {
        Tick();
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
        decimal => "number",
        string => "string",
        DateOnly => "date",
        bool => "bool",
        DurationValue => "duration",
        _ => "value",
    };

    private object? EvalCall(Call c, Scope scope, object?[] row)
    {
        object? Arg(int i) => Eval(c.Args[i], scope, row);
        void Arity(int min, int max)
        {
            if (c.Args.Count < min || c.Args.Count > max)
            {
                throw new QueryException($"{c.Name}() takes {(min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{min}-{max}")} argument(s).", c.Pos);
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
            case "dayofweek":
                Arity(1, 1);
                // "1 Mon" … "7 Sun": ISO order, so grouped output sorts Monday first.
                return D(0) is { } dw ? $"{((int)dw.DayOfWeek + 6) % 7 + 1} {dw.DayOfWeek.ToString()[..3]}" : null;
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
