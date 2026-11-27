namespace FlowLedger.Application.Features.Query;

// Column scope for resolving names while a query runs (#103: split out of QueryEngine.cs).
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
