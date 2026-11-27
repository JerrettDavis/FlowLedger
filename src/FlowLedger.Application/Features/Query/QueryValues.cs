using System.Diagnostics;
using System.Globalization;

namespace FlowLedger.Application.Features.Query;

// Value comparison and formatting helpers shared by the evaluator (#103: split out of QueryEngine.cs).
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
