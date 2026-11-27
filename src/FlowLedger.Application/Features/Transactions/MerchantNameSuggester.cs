using System.Globalization;
using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>
/// Turns a raw bank descriptor ("SQ *BLUE BOTTLE COFFEE #1234 SAN FRANCISCO CA 94103") into a readable merchant name
/// ("Blue Bottle Coffee"). Pure and conservative: returns null when it can't improve on the original.
/// </summary>
internal static partial class MerchantNameSuggester
{
    // Payment-processor / channel prefixes that are not part of the merchant name.
    [GeneratedRegex(@"^(?:sq\s*\*|tst\s*\*|pp\s*\*|paypal\s*\*|pos\s+(?:debit|purchase)?|debit\s+card\s+purchase|purchase\s+authorized\s+on\s+\d{1,2}/\d{1,2}|recurring\s+payment|ach\s+(?:debit|withdrawal)|checkcard|visa\s+purchase)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Prefix();

    // Trailing "ST 12345" / "ST" (US state code, optional zip) or a bare zip. The city is deliberately left in:
    // its word count can't be told apart from the merchant name.
    [GeneratedRegex(@"\s+(?:AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|DC)(?:\s+\d{5}(?:-\d{4})?)?$|\s+\d{5}(?:-\d{4})?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingLocation();

    [GeneratedRegex(@"\s*(?:#\s*\d+|\bstore\s*#?\s*\d+|\bno\.?\s*\d+)\b|\s+\d{3,}\b|\s*\*\s*[A-Z0-9]{5,}\b|\bx{2,}\d+\b|\b\d{1,2}/\d{1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Spaces();

    private static readonly HashSet<string> Keep = new(StringComparer.OrdinalIgnoreCase)
        { "LLC", "INC", "LTD", "USA", "HMF", "BP", "AT&T", "HVAC", "HOA", "ATM", "CVS", "QT", "IRS", "DMV", "GEICO", "T-MOBILE" };

    public static string? Suggest(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var original = description.Trim();
        var s = Prefix().Replace(original, string.Empty);
        s = Noise().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim();
        // Only strip a trailing "ST 12345" from shouting descriptors, where city words are unambiguous.
        if (s.Where(char.IsLetter).All(char.IsUpper))
        {
            var stripped = TrailingLocation().Replace(s, string.Empty).Trim();
            if (stripped.Length >= 2)
            {
                s = stripped;
            }
        }

        s = s.Trim(' ', '*', '-', '#', ',', '.');
        if (s.Length < 2)
        {
            return null;
        }

        var pretty = TitleCase(s);
        return string.Equals(pretty, original, StringComparison.Ordinal) ? null : pretty;
    }

    private static string TitleCase(string s)
    {
        // Only re-case when the source is shouting; mixed-case text is assumed to be intentional.
        if (s.Any(char.IsLower))
        {
            return s;
        }

        var ti = CultureInfo.InvariantCulture.TextInfo;
        return string.Join(' ', s.Split(' ').Select(w => Keep.Contains(w) ? w.ToUpperInvariant() : ti.ToTitleCase(w.ToLowerInvariant())));
    }
}
