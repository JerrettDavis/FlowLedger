using System.Text;
using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.LocalAi;

/// <summary>
/// Cleans a raw bank descriptor before it is embedded or compared: "SQ *BLUE BOTTLE COFFEE #1042 OAKLAND CA" and
/// "BLUE BOTTLE COFFEE 77" must land close together. Pure; never used to change stored data, only to compute similarity.
/// </summary>
internal static partial class DescriptorNormalizer
{
    [GeneratedRegex(@"^\s*(?:(?:sq|tst|pp|paypal|gglpay|google pay|apple pay|amzn mktp|pos|debit|credit|purchase|recurring|checkcard|check card|visa|ach|web|pmt|payment to)\s*[\*\-:#]+\s*|(?:pos|debit|purchase|recurring|checkcard|check card)\s+)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Prefixes();

    // Order/reference codes such as "2K4LP9" mix letters and digits; dropping them whole avoids stray one-letter fragments.
    [GeneratedRegex(@"\b(?=[a-z]*\d)(?=\d*[a-z])[a-z\d]{5,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Codes();

    [GeneratedRegex(@"[\d#\*_/\\\.,;:@!\?\(\)\[\]\{\}\|~`""'<>=+%&\-]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\b(?:inc|llc|ltd|co|corp|corporation|company|www|com|net|org)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Suffixes();

    [GeneratedRegex(@"\s+[a-z]{2}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingState();

    /// <summary>Lower-case, prefix/digit/punctuation-free, single-spaced text; empty when nothing meaningful is left.</summary>
    public static string Normalize(string? merchant, string? description)
    {
        var raw = !string.IsNullOrWhiteSpace(merchant) ? merchant! : description ?? string.Empty;
        if (raw.Length > 160)
        {
            raw = raw[..160];
        }

        var s = Prefixes().Replace(raw, " ");
        s = Codes().Replace(s, " ");
        s = Noise().Replace(s, " ");
        s = Suffixes().Replace(s, " ");
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        // A trailing two-letter token is usually a US state ("... oakland ca"); drop it only when more words remain.
        if (s.Count(c => c == ' ') >= 2)
        {
            s = TrailingState().Replace(s, string.Empty);
        }

        return Fold(s);
    }

    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).Trim();
    }
}
