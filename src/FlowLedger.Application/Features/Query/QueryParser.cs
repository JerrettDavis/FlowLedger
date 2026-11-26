using System.Globalization;
using System.Text;

namespace FlowLedger.Application.Features.Query;

/// <summary>A user-facing query error with the character offset where it was detected.</summary>
public sealed class QueryException(string message, int position = 0) : Exception(message)
{
    public int Position { get; } = position;
}

// ── AST ────────────────────────────────────────────────────────────────────────

internal abstract record Expr;
internal sealed record Literal(object? Value) : Expr;
internal sealed record DurationLiteral(int Amount, string Unit) : Expr;
internal sealed record ColumnRef(string Name, int Pos) : Expr;
internal sealed record Unary(string Op, Expr Operand) : Expr;
internal sealed record Binary(string Op, Expr Left, Expr Right) : Expr;
internal sealed record InList(Expr Value, IReadOnlyList<Expr> Items, bool Negated) : Expr;
internal sealed record Call(string Name, IReadOnlyList<Expr> Args, int Pos) : Expr;

internal sealed record NamedExpr(string Name, Expr Expr);
internal sealed record Aggregate(string Name, string Function, IReadOnlyList<Expr> Args, int Pos);
internal sealed record SortKey(Expr Expr, bool Descending);

internal abstract record Operator;
internal sealed record WhereOp(Expr Predicate) : Operator;
internal sealed record ProjectOp(IReadOnlyList<NamedExpr> Items) : Operator;
internal sealed record ExtendOp(IReadOnlyList<NamedExpr> Items) : Operator;
internal sealed record SummarizeOp(IReadOnlyList<Aggregate> Aggregates, IReadOnlyList<NamedExpr> By) : Operator;
internal sealed record SortOp(IReadOnlyList<SortKey> Keys) : Operator;
internal sealed record TakeOp(int Count) : Operator;
internal sealed record TopOp(int Count, SortKey Key) : Operator;
internal sealed record DistinctOp(IReadOnlyList<NamedExpr> Items) : Operator;
internal sealed record CountOp : Operator;

internal sealed record ParsedQuery(string Source, IReadOnlyList<Operator> Operators);

// ── Lexer ──────────────────────────────────────────────────────────────────────

internal enum Tok { Ident, Number, String, Duration, Symbol, End }

internal readonly record struct Token(Tok Kind, string Text, int Pos, decimal Number = 0, string Unit = "");

internal static class QueryLexer
{
    private static readonly string[] Durations = ["mo", "d", "w", "y"];

    public static List<Token> Lex(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Line comments: // …
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            var start = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                var numText = text[start..i];
                if (!decimal.TryParse(numText, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                {
                    throw new QueryException($"Invalid number '{numText}'.", start);
                }

                // Duration suffix: 30d, 2w, 6mo, 1y (only when the letters are exactly a unit).
                var j = i;
                while (j < text.Length && char.IsLetter(text[j]))
                {
                    j++;
                }

                var suffix = text[i..j];
                if (suffix.Length > 0 && Durations.Contains(suffix) && n == Math.Floor(n))
                {
                    tokens.Add(new Token(Tok.Duration, text[start..j], start, n, suffix));
                    i = j;
                    continue;
                }

                tokens.Add(new Token(Tok.Number, numText, start, n));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(Tok.Ident, text[start..i], start));
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                var sb = new StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                    if (text[i] == quote) { closed = true; i++; break; }
                    sb.Append(text[i++]);
                }
                if (!closed)
                {
                    throw new QueryException("Unterminated string literal.", start);
                }

                tokens.Add(new Token(Tok.String, sb.ToString(), start));
                continue;
            }

            // Two-char operators first.
            if (i + 1 < text.Length)
            {
                var two = text.Substring(i, 2);
                if (two is "==" or "!=" or "<>" or "<=" or ">=" or "&&" or "||")
                {
                    tokens.Add(new Token(Tok.Symbol, two, start));
                    i += 2;
                    continue;
                }
            }

            if ("|,()=<>+-*/%".Contains(c))
            {
                tokens.Add(new Token(Tok.Symbol, c.ToString(), start));
                i++;
                continue;
            }

            throw new QueryException($"Unexpected character '{c}'.", start);
        }

        tokens.Add(new Token(Tok.End, "", text.Length));
        return tokens;
    }
}

// ── Parser ─────────────────────────────────────────────────────────────────────

internal sealed class QueryParser
{
    private static readonly HashSet<string> AggregateFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "count", "sum", "avg", "min", "max", "dcount", "countif", "sumif" };

    private readonly List<Token> _t;
    private int _p;

    private QueryParser(List<Token> tokens) => _t = tokens;

    public static ParsedQuery Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new QueryException("Query is empty.");
        }

        if (text.Length > 4000)
        {
            throw new QueryException("Query is too long (4000 characters max).");
        }

        return new QueryParser(QueryLexer.Lex(text)).ParseQuery();
    }

    private Token Cur => _t[_p];
    private bool IsSymbol(string s) => Cur.Kind == Tok.Symbol && Cur.Text == s;
    private bool IsWord(string w) => Cur.Kind == Tok.Ident && Cur.Text.Equals(w, StringComparison.OrdinalIgnoreCase);

    private bool EatSymbol(string s)
    {
        if (!IsSymbol(s))
        {
            return false;
        }

        _p++;
        return true;
    }

    private bool EatWord(string w)
    {
        if (!IsWord(w))
        {
            return false;
        }

        _p++;
        return true;
    }

    private void ExpectSymbol(string s)
    {
        if (!EatSymbol(s))
        {
            throw new QueryException($"Expected '{s}' but found '{Describe(Cur)}'.", Cur.Pos);
        }
    }

    private static string Describe(Token t) => t.Kind == Tok.End ? "end of query" : t.Text;

    private ParsedQuery ParseQuery()
    {
        if (Cur.Kind != Tok.Ident)
        {
            throw new QueryException("A query starts with a dataset name, e.g. 'transactions | take 10'.", Cur.Pos);
        }

        var source = _t[_p++].Text.ToLowerInvariant();
        var ops = new List<Operator>();
        while (EatSymbol("|"))
        {
            ops.Add(ParseOperator());
        }

        if (Cur.Kind != Tok.End)
        {
            throw new QueryException($"Unexpected '{Describe(Cur)}'. Did you forget a '|' between operators?", Cur.Pos);
        }

        return new ParsedQuery(source, ops);
    }

    private Operator ParseOperator()
    {
        if (Cur.Kind != Tok.Ident)
        {
            throw new QueryException("Expected an operator (where, project, extend, summarize, sort, take, top, distinct, count).", Cur.Pos);
        }

        var word = Cur.Text.ToLowerInvariant();
        var pos = Cur.Pos;
        _p++;
        switch (word)
        {
            case "where":
            case "filter":
                return new WhereOp(ParseExpr());
            case "project":
                return new ProjectOp(ParseNamedList(requireName: false));
            case "extend":
                return new ExtendOp(ParseNamedList(requireName: true));
            case "distinct":
                return new DistinctOp(ParseNamedList(requireName: false));
            case "summarize":
            case "stats":
                return ParseSummarize();
            case "sort":
            case "order":
                if (!EatWord("by"))
                {
                    throw new QueryException("Expected 'by' after sort.", Cur.Pos);
                }

                return new SortOp(ParseSortKeys());
            case "take":
            case "limit":
            case "head":
                return new TakeOp(ParseCount());
            case "top":
                {
                    var n = ParseCount();
                    if (!EatWord("by"))
                    {
                        throw new QueryException("Expected 'by' after top N.", Cur.Pos);
                    }

                    var keys = ParseSortKeys();
                    if (keys.Count != 1)
                    {
                        throw new QueryException("top accepts a single sort key.", pos);
                    }

                    return new TopOp(n, keys[0]);
                }
            case "count":
                return new CountOp();
            default:
                throw new QueryException($"Unknown operator '{word}'.", pos);
        }
    }

    private int ParseCount()
    {
        if (Cur.Kind != Tok.Number || Cur.Number != Math.Floor(Cur.Number) || Cur.Number < 0)
        {
            throw new QueryException("Expected a whole number.", Cur.Pos);
        }

        var n = (int)Math.Min(Cur.Number, 1_000_000m);
        _p++;
        return n;
    }

    private List<SortKey> ParseSortKeys()
    {
        var keys = new List<SortKey>();
        do
        {
            var e = ParseExpr();
            var desc = true; // Kusto-style default: descending
            if (EatWord("asc"))
            {
                desc = false;
            }
            else if (EatWord("desc"))
            {
                desc = true;
            }

            keys.Add(new SortKey(e, desc));
        } while (EatSymbol(","));
        return keys;
    }

    private List<NamedExpr> ParseNamedList(bool requireName)
    {
        var items = new List<NamedExpr>();
        do
        {
            items.Add(ParseNamed(requireName));
        } while (EatSymbol(","));
        return items;
    }

    private NamedExpr ParseNamed(bool requireName)
    {
        // name = expr
        if (Cur.Kind == Tok.Ident && _t[_p + 1].Kind == Tok.Symbol && _t[_p + 1].Text == "=")
        {
            var name = Cur.Text;
            _p += 2;
            return new NamedExpr(name, ParseExpr());
        }

        if (requireName)
        {
            throw new QueryException("Expected 'name = expression'.", Cur.Pos);
        }

        var start = Cur.Pos;
        var e = ParseExpr();
        return new NamedExpr(DefaultName(e, start), e);
    }

    private static string DefaultName(Expr e, int pos) => e switch
    {
        ColumnRef c => c.Name,
        Call c => c.Args.Count > 0 && c.Args[0] is ColumnRef col ? $"{c.Name}_{col.Name}" : c.Name,
        _ => $"expr{pos}",
    };

    private SummarizeOp ParseSummarize()
    {
        var aggs = new List<Aggregate>();
        do
        {
            string? name = null;
            if (Cur.Kind == Tok.Ident && _t[_p + 1].Kind == Tok.Symbol && _t[_p + 1].Text == "=")
            {
                name = Cur.Text;
                _p += 2;
            }

            if (Cur.Kind != Tok.Ident || !AggregateFunctions.Contains(Cur.Text))
            {
                throw new QueryException("Expected an aggregate: count(), sum(x), avg(x), min(x), max(x), dcount(x), countif(cond), sumif(x, cond).", Cur.Pos);
            }

            var fn = Cur.Text.ToLowerInvariant();
            var pos = Cur.Pos;
            _p++;
            ExpectSymbol("(");
            var args = new List<Expr>();
            if (!IsSymbol(")"))
            {
                do { args.Add(ParseExpr()); } while (EatSymbol(","));
            }
            ExpectSymbol(")");
            ValidateAggregateArity(fn, args.Count, pos);

            name ??= args.Count > 0 && args[0] is ColumnRef cr ? $"{fn}_{cr.Name}" : fn;
            aggs.Add(new Aggregate(name, fn, args, pos));
        } while (EatSymbol(","));

        var by = new List<NamedExpr>();
        if (EatWord("by"))
        {
            by = ParseNamedList(requireName: false);
        }

        return new SummarizeOp(aggs, by);
    }

    private static void ValidateAggregateArity(string fn, int n, int pos)
    {
        var expected = fn switch { "count" => 0, "sumif" => 2, _ => 1 };
        if (n != expected)
        {
            throw new QueryException($"{fn}() takes {expected} argument(s) but got {n}.", pos);
        }
    }

    // ── Expressions (precedence climbing) ───────────────────────────────────

    private int _depth;

    private Expr ParseExpr()
    {
        if (++_depth > 64)
        {
            throw new QueryException("Expression is nested too deeply.", Cur.Pos);
        }

        try { return ParseOr(); }
        finally { _depth--; }
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (IsWord("or") || IsSymbol("||"))
        {
            _p++;
            left = new Binary("or", left, ParseAnd());
        }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (IsWord("and") || IsSymbol("&&"))
        {
            _p++;
            left = new Binary("and", left, ParseNot());
        }
        return left;
    }

    private Expr ParseNot()
        => EatWord("not") ? new Unary("not", ParseNot()) : ParseComparison();

    private Expr ParseComparison()
    {
        var left = ParseAdditive();
        while (true)
        {
            if (Cur.Kind == Tok.Symbol && Cur.Text is "==" or "=" or "!=" or "<>" or "<" or "<=" or ">" or ">=")
            {
                var op = Cur.Text switch { "=" => "==", "<>" => "!=", var o => o };
                _p++;
                left = new Binary(op, left, ParseAdditive());
                continue;
            }

            var negated = false;
            var save = _p;
            if (EatWord("not"))
            {
                negated = true;
            }

            if (EatWord("in"))
            {
                ExpectSymbol("(");
                var items = new List<Expr>();
                do { items.Add(ParseAdditive()); } while (EatSymbol(","));
                ExpectSymbol(")");
                left = new InList(left, items, negated);
                continue;
            }

            if (Cur.Kind == Tok.Ident && Cur.Text.ToLowerInvariant() is "contains" or "startswith" or "endswith" or "has")
            {
                var op = string.Equals(Cur.Text, "has", StringComparison.OrdinalIgnoreCase) ? "contains" : Cur.Text.ToLowerInvariant();
                _p++;
                Expr e = new Binary(op, left, ParseAdditive());
                left = negated ? new Unary("not", e) : e;
                continue;
            }

            if (EatWord("between"))
            {
                ExpectSymbol("(");
                var lo = ParseAdditive();
                if (!EatWord("and"))
                {
                    ExpectSymbol(",");
                }

                var hi = ParseAdditive();
                ExpectSymbol(")");
                Expr e = new Binary("and", new Binary(">=", left, lo), new Binary("<=", left, hi));
                left = negated ? new Unary("not", e) : e;
                continue;
            }

            _p = save; // a stray 'not' that did not introduce an operator
            return left;
        }
    }

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Cur.Kind == Tok.Symbol && Cur.Text is "+" or "-")
        {
            var op = Cur.Text;
            _p++;
            left = new Binary(op, left, ParseMultiplicative());
        }
        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Cur.Kind == Tok.Symbol && Cur.Text is "*" or "/" or "%")
        {
            var op = Cur.Text;
            _p++;
            left = new Binary(op, left, ParseUnary());
        }
        return left;
    }

    private Expr ParseUnary()
        => EatSymbol("-") ? new Unary("-", ParseUnary()) : ParsePrimary();

    private Expr ParsePrimary()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case Tok.Number:
                _p++;
                return new Literal(t.Number);
            case Tok.String:
                _p++;
                return new Literal(t.Text);
            case Tok.Duration:
                _p++;
                return new DurationLiteral((int)t.Number, t.Unit);
            case Tok.Symbol when t.Text == "(":
                {
                    _p++;
                    var e = ParseExpr();
                    ExpectSymbol(")");
                    return e;
                }
            case Tok.Ident:
                {
                    _p++;
                    var lower = t.Text.ToLowerInvariant();
                    if (lower == "true")
                    {
                        return new Literal(true);
                    }

                    if (lower == "false")
                    {
                        return new Literal(false);
                    }

                    if (lower == "null")
                    {
                        return new Literal(null);
                    }

                    if (EatSymbol("("))
                    {
                        var args = new List<Expr>();
                        if (!IsSymbol(")"))
                        {
                            do { args.Add(ParseExpr()); } while (EatSymbol(","));
                        }
                        ExpectSymbol(")");
                        return new Call(lower, args, t.Pos);
                    }

                    return new ColumnRef(t.Text, t.Pos);
                }
            default:
                throw new QueryException($"Unexpected '{Describe(t)}'.", t.Pos);
        }
    }
}
