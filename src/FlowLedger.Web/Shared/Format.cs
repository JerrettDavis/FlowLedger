using System.Globalization;
using MudBlazor;

namespace FlowLedger.Web.Shared;

/// <summary>
/// Format helpers for currency, dates, and color coding.
/// </summary>
public static class Format
{
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Format a decimal as currency (e.g., "$1,234.56").
    /// </summary>
    public static string Money(decimal v) =>
        v.ToString("C2", UsCulture);

    /// <summary>
    /// Format a decimal as signed currency (e.g., "+$1,234.56" or "−$1,234.56").
    /// Uses a proper minus sign (−, U+2212) for negative values.
    /// </summary>
    public static string Signed(decimal v) =>
        (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("C2", UsCulture);

    /// <summary>
    /// Format an absolute amount as signed currency based on direction.
    /// Credits (isCredit=true) are formatted with "+" prefix; debits with "−".
    /// </summary>
    public static string Signed(decimal amount, bool isCredit) =>
        Signed(isCredit ? amount : -amount);

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
