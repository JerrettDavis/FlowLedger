using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Categories;

/// <summary>
/// Built-in, deterministic keyword rules that suggest a category path for a transaction. Pure and side-effect free.
/// It only ever fills in a MISSING category; user choices and learned merchants are applied by the caller first.
/// Rules are ordered: the first match wins, so specific rules come before broad ones.
/// </summary>
public static class AutoCategorizer
{
    public const string TransferCategory = "Transfer";

    private sealed record Rule(string Path, Regex Pattern, string? Direction = null);

    private static Rule R(string path, string words, string? direction = null) =>
        new(path, new Regex("(?:" + words + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), direction);

    // Patterns are regex fragments in verbatim strings. Short or ambiguous words carry explicit word boundaries;
    // long distinctive stems (e.g. "fuel") deliberately match inside compound names such as "QuickFuel".
    private static readonly Rule[] Rules =
    [
        R("Income/Interest", @"interest|dividend", "Credit"),
        R("Income/Paycheck", @"payroll|salary|direct dep|paycheck|employer", "Credit"),
        R("Housing/Mortgage", @"mortgage|home loan"),
        R("Housing/Rent", @"\brent\b|landlord|property mgmt|\bhoa\b"),
        R("Bills/Insurance", @"insurance|geico|state farm|allstate"),
        R("Health/Medical", @"medical|clinic|dental|dentist|hospital|pharmacy|\bcvs|walgreens|health(?!y)|physician"),
        R("Bills/Utilities", @"electric|utilit|\bwater\b|\bpower\b|energy|sewer|trash"),
        R("Bills/Internet & Phone", @"internet|wireless|mobile|comcast|verizon|at&t|t-mobile|spectrum|broadband|fiber"),
        R("Subscriptions", @"stream|music|storage|netflix|spotify|hulu|disney\+|cloud|subscription|fittrack|prime video|patreon"),
        R("Transport/Fuel", @"fuel|gas station|\bshell\b|chevron|exxon|\bbp\b|sunoco|quicktrip|petrol"),
        R("Transport/Public & Rideshare", @"transit|rideshare|\buber|\blyft|shuttle|\bmetro|\btaxi|\btrain\b|amtrak|\bbus\b"),
        R("Transport/Parking", @"parking|garage|\btoll\b|\bpark\b"),
        R("Food/Groceries", @"grocery|groceries|supermarket|whole foods|trader joe|kroger|safeway|aldi|costco|publix|wegmans|instacart|bluejay"),
        R("Food/Dining", @"cafe|coffee|bistro|grill|pizza|taco|noodle|restaurant|kitchen|diner|\bbar\b|eatery|starbucks|mcdonald|chipotle|doordash|grubhub|burger|sushi|bakery|tavern"),
        R("Entertainment", @"cinema|movie|theater|theatre|concert|ticket|\bgames?\b|\bsteam\b|bowling"),
        R("Travel", @"airline|airways|hotel|airbnb|resort|expedia|\bdelta\b|\bunited\b"),
        R("Shopping", @"online|electronics|shop|amazon|\btarget\b|walmart|best buy|ebay|etsy|store|mart\b|market"),
        R(TransferCategory, @"transfer|payment thank you|autopay|credit card payment"),
    ];

    /// <summary>Suggested category path, or null when nothing matches.</summary>
    public static string? Categorize(string? merchant, string? description, string direction)
    {
        var text = string.Join(' ', new[] { merchant, description }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (text.Length == 0)
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            if (rule.Direction is not null && !string.Equals(rule.Direction, direction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.Pattern.IsMatch(text))
            {
                return rule.Path;
            }
        }

        return null;
    }

    /// <summary>Stable key for "same merchant": merchant name when known, else the description, upper-cased and trimmed.</summary>
    public static string MerchantKey(string? merchant, string? description) =>
        (string.IsNullOrWhiteSpace(merchant) ? description : merchant)?.Trim().ToUpperInvariant() ?? string.Empty;
}
