namespace FlowLedger.Web.Components.Shared;

/// <summary>Stable colour per top-level category, so "Food" and "Food/Dining" share a hue everywhere they appear.</summary>
public static class CategoryColors
{
    private static readonly string[] Palette =
        ["#4C9AFF", "#36B37E", "#FFAB00", "#FF5630", "#6554C0", "#00B8D9", "#F06292", "#8BC34A", "#FF8A65", "#9575CD"];

    public static string For(string? path)
    {
        var top = (path ?? string.Empty).Split('/')[0].Trim().ToUpperInvariant();
        if (top.Length == 0 || top == "UNCATEGORIZED")
        {
            return "#7f8794";
        }

        // FNV-1a: deterministic across runs (string.GetHashCode is randomized per process).
        var hash = 2166136261u;
        foreach (var ch in top)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return Palette[hash % (uint)Palette.Length];
    }
}
