namespace FlowLedger.Application.Features.Query;

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
