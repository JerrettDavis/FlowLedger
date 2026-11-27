namespace FlowLedger.Application.Features.RecurringFlows;

/// <summary>
/// Reduces a bank descriptor to the merchant's stable identity. The same subscription shows up as
/// <c>PP*GOOGLE LLC GOOGLE W MOUNTAIN VI CA</c> one month and <c>PP*GOOGLE LLC GOOGLE W 4029357733 CA</c> the next, so
/// processor prefixes (PayPal, Square, Toast), phone/reference numbers and trailing state codes are stripped. What is left
/// may still carry a city; the detector handles that by merging a key into any longer key that starts with it.
/// </summary>
internal static class MerchantNormalizer
{
    private static readonly HashSet<string> ProcessorPrefixes = new(StringComparer.Ordinal)
    {
        "PP", "PAYPAL", "SQ", "SQUARE", "TST", "PY", "SP", "DD", "PAYPAL.COM",
    };

    private static readonly HashSet<string> TrailingCodes = new(StringComparer.Ordinal)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS",
        "MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV",
        "WI","WY","DC","SGP","HKG","GBR","IRL","CAN","AUS","DEU","FRA","NLD","USA",
    };

    /// <summary>Upper-case merchant tokens with processor prefix, reference numbers and trailing state/country removed.</summary>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        var tokens = (text ?? string.Empty)
            .Replace('*', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(',', ';', '#').ToUpperInvariant())
            .Where(t => t.Length > 0)
            .ToList();

        while (tokens.Count > 1 && ProcessorPrefixes.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        var cut = tokens.Count > 1 ? tokens.FindIndex(1, t => t.Any(char.IsDigit)) : -1;
        if (cut > 0)
        {
            tokens.RemoveRange(cut, tokens.Count - cut);
        }

        if (tokens.Count > 1 && TrailingCodes.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens;
    }

    /// <summary>Space-joined tokens: the readable merchant identity.</summary>
    public static string Normalize(string? text) => string.Join(' ', Tokens(text));

    /// <summary>Letters-only identity used in suppression/grouping keys.</summary>
    public static string Key(string? text)
        => new(Normalize(text).Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
}
