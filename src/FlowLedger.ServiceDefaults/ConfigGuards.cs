namespace Microsoft.Extensions.Hosting;

/// <summary>Shared checks for production configuration, used by every host's startup validation.</summary>
public static class ConfigGuards
{
    /// <summary>
    /// True for values that were copied from a template and never filled in (<c>your-api-key-here</c>, <c>changeme</c>,
    /// <c>&lt;POSTGRES_PASSWORD&gt;</c>, ...). Null/empty is NOT a placeholder; callers decide whether blank is allowed.
    /// </summary>
    public static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        return v.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("your_", StringComparison.OrdinalIgnoreCase)
            || v.EndsWith("-here", StringComparison.OrdinalIgnoreCase)
            || v.Contains("changeme", StringComparison.OrdinalIgnoreCase)
            || v.Contains("change-me", StringComparison.OrdinalIgnoreCase)
            || v.Contains("replace-me", StringComparison.OrdinalIgnoreCase)
            || (v.Contains('<') && v.Contains('>'))
            || v.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || v.Equals("password", StringComparison.OrdinalIgnoreCase);
    }
}
