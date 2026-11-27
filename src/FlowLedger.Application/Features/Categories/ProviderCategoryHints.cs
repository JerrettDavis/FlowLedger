using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Categories;

/// <summary>
/// Maps the free-text category a bank aggregator sends ("Food &amp; Dining", "Gas &amp; Fuel", "Mortgage &amp; Rent") onto the built-in
/// category paths. It is only a hint: it runs after learned merchants and keyword rules, and the result is an Auto category the
/// user can overwrite. Labels that mean nothing specific ("Uncategorized", "Shopping", "Other") give no hint.
/// </summary>
internal static class ProviderCategoryHints
{
    private static readonly (Regex Pattern, string Path, bool CreditOnly)[] Rules =
    [
        (Rx("paycheck|salary|payroll"), "Income/Paycheck", true),
        (Rx("interest income|dividend"), "Income/Interest", true),
        (Rx("mortgage"), "Housing/Mortgage", false),
        (Rx(@"\brent\b"), "Housing/Rent", false),
        (Rx("home improvement|home maintenance|lawn"), "Housing/Home Maintenance", false),
        (Rx("insurance"), "Bills/Insurance", false),
        (Rx("utilit|electric|gas & electric|water"), "Bills/Utilities", false),
        (Rx("internet|phone|cable|mobile"), "Bills/Internet & Phone", false),
        (Rx("doctor|medical|health|pharmacy|dentist"), "Health/Medical", false),
        (Rx("grocer|supermarket"), "Food/Groceries", false),
        (Rx(@"restaurant|dining|coffee|fast food|\bbars?\b"), "Food/Dining", false),
        (Rx("gas & fuel|gas station|\\bfuel\\b|auto fuel"), "Transport/Fuel", false),
        (Rx(@"parking|\btolls?\b"), "Transport/Parking", false),
        (Rx("public transport|rideshare|taxi|ride share"), "Transport/Public & Rideshare", false),
        (Rx("travel|airfare|hotel|vacation"), "Travel", false),
        (Rx(@"entertainment|movies|music|\barts\b|recreation"), "Entertainment", false),
        (Rx("subscription|streaming"), "Subscriptions", false),
        (Rx("bank fee|service fee|atm fee|fees? & charges|overdraft"), "Fees/Bank Fees", false),
        (Rx("kids|education|school|child"), "Family/Kids & School", false),
    ];

    /// <summary>The built-in category path this provider label points at, or null when it says nothing specific.</summary>
    public static string? PathFor(string? providerCategory, string direction)
    {
        if (string.IsNullOrWhiteSpace(providerCategory))
        {
            return null;
        }

        var isCredit = string.Equals(direction, "Credit", StringComparison.OrdinalIgnoreCase);
        foreach (var (pattern, path, creditOnly) in Rules)
        {
            if (pattern.IsMatch(providerCategory) && (!creditOnly || isCredit))
            {
                return path;
            }
        }

        return null;
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
