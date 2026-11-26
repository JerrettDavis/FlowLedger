using System.Globalization;

namespace FlowLedger.Web.Shared;

/// <summary>
/// Display-only currency and locale preference (no FX: amounts are never converted, only formatted differently).
/// Stored per browser/user in the <see cref="CookieName"/> cookie as "CURRENCY|locale"; default USD / en-US.
/// </summary>
public sealed record DisplaySettings(string Currency, string Locale)
{
    public const string CookieName = "fl-display";

    public static DisplaySettings Default { get; } = new("USD", "en-US");

    /// <summary>Locales offered in the UI.</summary>
    public static IReadOnlyList<string> SupportedLocales { get; } =
        ["en-US", "en-GB", "en-CA", "en-AU", "de-DE", "fr-FR", "es-ES", "ja-JP"];

    /// <summary>Currencies offered in the UI.</summary>
    public static IReadOnlyList<string> SupportedCurrencies { get; } =
        ["USD", "EUR", "GBP", "CAD", "AUD", "JPY", "CHF", "MXN", "INR"];

    public string ToCookieValue() => $"{Currency}|{Locale}";

    /// <summary>Parses a cookie value; anything missing, malformed or unsupported falls back to the default per part.</summary>
    public static DisplaySettings Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        var parts = value.Split('|');
        var currency = parts[0].Trim().ToUpperInvariant();
        var requested = parts.Length > 1 ? parts[1].Trim() : "";
        var locale = SupportedLocales.FirstOrDefault(l => string.Equals(l, requested, StringComparison.OrdinalIgnoreCase));
        return new DisplaySettings(
            SupportedCurrencies.Contains(currency) ? currency : Default.Currency,
            locale ?? Default.Locale);
    }

    /// <summary>
    /// A culture for the locale whose currency symbol and decimals follow the chosen currency, so plain
    /// <c>"C"</c> formatting on the current thread shows the display currency.
    /// </summary>
    public CultureInfo ToCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(Locale).Clone();
        Format.ApplyCurrency(culture.NumberFormat, Currency);
        return culture;
    }
}
