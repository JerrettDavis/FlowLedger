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
        // Transfers first: "Credit Card Payment" must not read as a card bill, nor "Transfer for Cash Spending" as spending.
        (Rx(@"credit card payment|^transfer|cash spending"), AutoCategorizer.TransferCategory, false),

        // Income (credits only; the same words on a debit are not income).
        (Rx("paycheck|salary|payroll"), "Income/Paycheck", true),
        (Rx(@"\bbonus\b"), "Income/Bonus", true),
        (Rx("rental income"), "Income/Rental Income", true),
        (Rx("reimbursement"), "Income/Reimbursement", true),
        (Rx("returned purchase"), "Income/Returned Purchase", true),
        (Rx("interest income|dividend"), "Income/Interest", true),
        (Rx(@"^(buy|sell)$"), "Investments/Trades", false),

        // Housing
        (Rx("mortgage"), "Housing/Mortgage", false),
        (Rx(@"\brent\b"), "Housing/Rent", false),
        (Rx("furnishings"), "Housing/Furnishings", false),
        (Rx("home supplies"), "Housing/Home Supplies", false),
        (Rx("home improvement|home maintenance|home services|lawn"), "Housing/Home Maintenance", false),

        // Bills
        (Rx("insurance"), "Bills/Insurance", false),
        (Rx("utilit|electric|gas & electric|water"), "Bills/Utilities", false),
        (Rx("internet|phone|cable|mobile"), "Bills/Internet & Phone", false),
        (Rx("television"), "Subscriptions", false),

        // Education (before the generic school/kids rule)
        (Rx("tuition"), "Education/Tuition", false),
        (Rx("student loan"), "Education/Student Loan", false),
        (Rx("books & supplies"), "Education/Books & Supplies", false),

        // Health
        (Rx(@"\bgym\b"), "Health/Gym", false),
        (Rx(@"^sports$"), "Health/Sports", false),
        (Rx("doctor|medical|health|pharmacy|dentist|eyecare"), "Health/Medical", false),

        // Food
        (Rx("grocer|supermarket"), "Food/Groceries", false),
        (Rx("alcohol"), "Food/Alcohol & Bars", false),
        (Rx(@"restaurant|dining|coffee|fast food|\bbars?\b"), "Food/Dining", false),

        // Transport
        (Rx(@"gas & fuel|gas station|\bfuel\b|auto fuel|^gas$"), "Transport/Fuel", false),
        (Rx(@"auto payment|car payment|auto loan"), "Transport/Auto Loan", false),
        (Rx(@"service & parts"), "Transport/Service & Parts", false),
        (Rx(@"parking|\btolls?\b"), "Transport/Parking", false),
        (Rx(@"rental car"), "Travel", false),
        (Rx("public transport|rideshare|taxi|ride share"), "Transport/Public & Rideshare", false),
        (Rx("travel|airfare|hotel|vacation"), "Travel", false),

        (Rx(@"entertainment|movies|music|\barts\b|recreation|amusement"), "Entertainment", false),
        (Rx("subscription|streaming|newspapers|magazines"), "Subscriptions", false),

        // Fees (the specific kinds before the catch-all)
        (Rx("finance charge"), "Fees/Finance Charge", false),
        (Rx("late fee"), "Fees/Late Fee", false),
        (Rx("trade commission"), "Fees/Trade Commissions", false),
        (Rx("service fee"), "Fees/Service Fee", false),
        (Rx("bank fee|atm fee|fees? & charges|overdraft"), "Fees/Bank Fees", false),

        // Family, with the specific kinds before the generic kids/school rule
        (Rx("allowance"), "Family/Allowance", false),
        (Rx("baby supplies"), "Family/Baby Supplies", false),
        (Rx("child support"), "Family/Child Support", false),
        (Rx(@"\btoys\b"), "Family/Toys", false),
        (Rx("kids|education|school|child|daycare"), "Family/Kids & School", false),

        // Taxes, pets, personal care, shopping and gifts: only the specific MX sub-categories (their top-level labels say too little)
        (Rx("federal tax"), "Taxes/Federal Tax", false),
        (Rx("state tax"), "Taxes/State Tax", false),
        (Rx("local tax"), "Taxes/Local Tax", false),
        (Rx("property tax"), "Taxes/Property Tax", false),
        (Rx("sales tax"), "Taxes/Sales Tax", false),
        (Rx("pet food"), "Pets/Pet Food & Supplies", false),
        (Rx("pet grooming"), "Pets/Pet Grooming", false),
        (Rx("veterinar"), "Pets/Veterinary", false),
        (Rx(@"^hair$"), "Personal Care/Hair", false),
        (Rx(@"^laundry$"), "Personal Care/Laundry", false),
        (Rx("spa & massage"), "Personal Care/Spa & Massage", false),
        (Rx(@"^clothing$"), "Shopping/Clothing", false),
        (Rx("electronics & software"), "Shopping/Electronics & Software", false),
        (Rx(@"^hobbies$"), "Shopping/Hobbies", false),
        (Rx("sporting goods"), "Shopping/Sporting Goods", false),
        (Rx("charity"), "Gifts & Donations/Charity", false),
        (Rx(@"^gifts?$"), "Gifts & Donations/Gift", false),
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
