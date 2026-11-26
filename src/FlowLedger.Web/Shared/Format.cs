using System.Globalization;
using MudBlazor;

namespace FlowLedger.Web.Shared;

/// <summary>
/// Format helpers for currency, dates, and color coding.
/// </summary>
public static class Format
{
    /// <summary>
    /// Format a decimal as currency in the display currency/locale (default "$1,234.56"; see <see cref="DisplaySettings"/>).
    /// </summary>
    public static string Money(decimal v) =>
        v.ToString("C", CultureInfo.CurrentCulture);

    private static readonly Dictionary<string, string> CurrencySymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["JPY"] = "¥",
        ["CNY"] = "CN¥",
        ["INR"] = "₹",
        ["KRW"] = "₩",
        ["CAD"] = "CA$",
        ["AUD"] = "A$",
        ["NZD"] = "NZ$",
        ["MXN"] = "MX$",
        ["BRL"] = "R$",
        ["CHF"] = "CHF",
    };

    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase) { "JPY", "KRW" };

    /// <summary>
    /// Format an amount in its own currency using <paramref name="culture"/> for separators and symbol placement
    /// (default en-US). A missing currency keeps the legacy USD rendering; an unknown code is shown as the code,
    /// never as "$".
    /// </summary>
    public static string Money(decimal v, string? currency, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (string.IsNullOrWhiteSpace(currency))
        {
            return v.ToString("C", culture);
        }

        var nf = (NumberFormatInfo)culture.NumberFormat.Clone();
        ApplyCurrency(nf, currency.Trim().ToUpperInvariant());
        return v.ToString("C", nf);
    }

    /// <summary>Sets symbol and decimals on <paramref name="nf"/> for an ISO currency code (unknown codes show as the code).</summary>
    internal static void ApplyCurrency(NumberFormatInfo nf, string code)
    {
        if (CurrencySymbols.TryGetValue(code, out var symbol))
        {
            nf.CurrencySymbol = symbol;
        }
        else
        {
            nf.CurrencySymbol = code;
            nf.CurrencyPositivePattern = 3; // "n XYZ"
            nf.CurrencyNegativePattern = 8; // "-n XYZ"
        }

        nf.CurrencyDecimalDigits = ZeroDecimalCurrencies.Contains(code) ? 0 : 2;
    }

    /// <summary>
    /// Format a decimal as signed currency (e.g., "+$1,234.56" or "−$1,234.56").
    /// Uses a proper minus sign (−, U+2212) for negative values.
    /// </summary>
    public static string Signed(decimal v) =>
        (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("C", CultureInfo.CurrentCulture);

    /// <summary>
    /// Format an absolute amount as signed currency based on direction.
    /// Credits (isCredit=true) are formatted with "+" prefix; debits with "−".
    /// </summary>
    public static string Signed(decimal amount, bool isCredit) =>
        Signed(isCredit ? amount : -amount);

    /// <summary>
    /// Signed percentage change of <paramref name="change"/> relative to <paramref name="baseline"/>
    /// (e.g., "+4.2%"). Returns an empty string when the baseline is zero (change is undefined).
    /// The percentage is relative to the magnitude of the baseline so a debt shrinking reads as positive.
    /// </summary>
    public static string Percent(decimal change, decimal baseline)
    {
        if (baseline == 0)
        {
            return "";
        }

        var pct = change / Math.Abs(baseline) * 100m;
        var rounded = Math.Round(pct, pct is > -10 and < 10 ? 2 : 1);
        return (rounded >= 0 ? "+" : "−") + Math.Abs(rounded).ToString("0.##", CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>"+$12.00 (+1.2%)" - signed amount with its percentage change when computable.</summary>
    public static string SignedWithPercent(decimal change, decimal baseline)
    {
        var p = Percent(change, baseline);
        return p.Length == 0 ? Signed(change) : $"{Signed(change)} ({p})";
    }

    /// <summary>
    /// Format a DateOnly as "MMM d" (e.g., "Jan 15").
    /// </summary>
    public static string DayMonth(DateOnly d) =>
        d.ToString("MMM d", CultureInfo.CurrentCulture);

    /// <summary>
    /// Format a DateOnly as "MMM d, yyyy" (e.g., "Jan 15, 2025").
    /// </summary>
    public static string FullDate(DateOnly d) =>
        d.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>
    /// Format a DateTime as "MMM d" (e.g., "Jan 15").
    /// </summary>
    public static string DayMonth(DateTime d) =>
        DateOnly.FromDateTime(d).ToString("MMM d", CultureInfo.CurrentCulture);

    /// <summary>
    /// Format a DateTime as "MMM d, yyyy" (e.g., "Jan 15, 2025").
    /// </summary>
    public static string FullDate(DateTime d) =>
        DateOnly.FromDateTime(d).ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>
    /// Return a MudBlazor Color for a delta value: Success if >= 0, Error if < 0.
    /// </summary>
    public static Color Delta(decimal v) =>
        v >= 0 ? Color.Success : Color.Error;
}
