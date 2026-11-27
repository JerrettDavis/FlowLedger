using System.Text.RegularExpressions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Categories;

/// <summary>
/// Built-in, deterministic keyword rules that suggest a category path for a transaction. Pure and side-effect free.
/// It only ever fills in a MISSING category; user choices and learned merchants are applied by the caller first.
/// Rules are ordered: the first match wins, so specific rules come before broad ones.
/// </summary>
internal static class AutoCategorizer
{
    public const string TransferCategory = "Transfer";

    private sealed record Rule(string Path, Regex Pattern, string? Direction = null);

    private static Rule R(string path, string words, string? direction = null) =>
        new(path, new Regex("(?:" + words + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), direction);

    // Patterns are regex fragments in verbatim strings. Short or ambiguous words carry explicit word boundaries;
    // long distinctive stems (e.g. "fuel") deliberately match inside compound names such as "QuickFuel".
    private static readonly Rule[] Rules =
    [
        // Money moving between the user's own accounts / paying off cards comes first, so words like "online"
        // in "ONLINE PAYMENT, THANK YOU" can never be mistaken for a purchase.
        R(TransferCategory, @"transfer|payment,? thank you|autopay|credit card payment|credit crd|card online payment|\bepay\b|online payment|monthly payment|additional principal"),
        // Brokerage activity is not income or spending: reinvestments and trades just move money within the portfolio.
        R("Investments/Trades", @"dividend reinvest|reinvest|^\s*(buy|sell)\b|\bsell\s*-|\bbuy\s*-"),
        R("Income/Interest", @"interest|dividend", nameof(TransactionDirection.Credit)),
        R("Income/Paycheck", @"payroll|salary|direct dep|paycheck|employer", nameof(TransactionDirection.Credit)),
        R("Income/Deposits", @"chk deposit|mobile deposit|deposit", nameof(TransactionDirection.Credit)),
        R("Fees/Bank Fees", @"atm usage fee|atm fee|service charge|overdraft|maintenance fee|\bfee\b"),
        R("Housing/Mortgage", @"mortgage|home loan"),
        R("Housing/Rent", @"\brent\b|landlord|property mgmt|\bhoa\b"),
        R("Housing/Home Maintenance", @"mowing|\blawn\b|landscap|home depot|lowe'?s|plumb|\bhvac\b|pest|goodleap"),
        R("Bills/Insurance", @"insurance|geico|state farm|allstate|prudential"),
        R("Health/Medical", @"medical|clinic|dental|dentist|hospital|pharmacy|\bcvs|walgreens|health(?!y)|physician|my ?chart|moodcenter|\badhd\b|therap"),
        R("Bills/Utilities", @"electric|utilit|\bwater\b|\bpower\b|\bpwr\b|natural gas|energy|sewer|trash|city of"),
        R("Bills/Internet & Phone", @"internet|wireless|mobile|comcast|verizon|\batt\b|at&t|t-mobile|spectrum|broadband|fiber"),
        R("Subscriptions", @"stream|music|storage|netflix|spotify|hulu|disney\+|cloud|subscription|fittrack|prime video|patreon|anthropic|openai|chatgpt|ghost\.org|invoiceninja|nanonoble|simplefin|github|jetbrains|digitalocean|adobe|google (llc|w)|google workspace"),
        R("Transport/Auto Loan", @"\bhmf\b|hmfusa|auto fin|toyota fin|honda fin|ford credit|stellantis|ally auto"),
        R("Transport/Fuel", @"fuel|gas station|\bshell\b|chevron|exxon|\bbp\b|sunoco|quicktrip|quiktrip|\bqt\b|petrol|love'?s\b|electrify|evgo|chargepoint|nyx\*"),
        R("Other/Peer Payments", @"cash app|venmo|zelle"),
        R("Transport/Public & Rideshare", @"transit|rideshare|\buber|\blyft|shuttle|\bmetro|\btaxi|\btrain\b|amtrak|\bbus\b"),
        R("Transport/Parking", @"parking|garage|\btoll\b|\bpark\b"),
        R("Family/Kids & School", @"lineleader|scholastic|swim school|\bschool\b|playtime|daycare|tutor"),
        R("Food/Groceries", @"grocery|groceries|supermarket|whole foods|trader joe|kroger|safeway|aldi|costco|publix|wegmans|instacart|bluejay|sams club"),
        R("Food/Dining", @"cafe|coffee|bistro|grill|pizza|taco|noodle|restaurant|kitchen|diner|\bbar\b|eatery|starbucks|mcdonald|chipotle|doordash|grubhub|burger|sushi|bakery|tavern|quiznos|potbelly|torchy|chicken|waffle|moxie|creame|\bchef\b|senor|\bida red\b|dipper|brau"),
        R("Entertainment", @"cinema|movie|theater|theatre|concert|ticket|\bgames?\b|\bsteam\b|bowling|museum|cigar"),
        R("Travel", @"airline|airways|hotel|airbnb|resort|expedia|\bdelta\b|\bunited\b"),
        R("Shopping", @"electronics|shop|amazon|amzn|mktpl|\btarget\b|walmart|wm supercenter|best buy|ebay|etsy|store|mart\b|market"),
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
