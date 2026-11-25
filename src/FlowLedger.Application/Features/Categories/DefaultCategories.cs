namespace FlowLedger.Application.Features.Categories;

/// <summary>
/// The default category set: every original Mint category, each mapped to FlowLedger's closest established path where one
/// exists (the paths <see cref="AutoCategorizer"/> already emits, e.g. Mint "Groceries" -> "Food/Groceries"), otherwise
/// to a new "Group/Name" path using Mint's own names. Seeding is additive and idempotent: existing paths are never touched.
/// </summary>
public static class DefaultCategories
{
    /// <summary>Mint "Group > Category" and the FlowLedger path it maps to.</summary>
    public sealed record MintMapping(string MintGroup, string MintCategory, string Path);

    private static MintMapping M(string group, string category, string? path = null) =>
        new(group, category, path ?? $"{group}/{category}");

    public static readonly IReadOnlyList<MintMapping> Mint =
    [
        // Auto & Transport
        M("Auto & Transport", "Auto Insurance", "Bills/Insurance"),
        M("Auto & Transport", "Auto Payment", "Transport/Auto Loan"),
        M("Auto & Transport", "Gas & Fuel", "Transport/Fuel"),
        M("Auto & Transport", "Parking", "Transport/Parking"),
        M("Auto & Transport", "Public Transportation", "Transport/Public & Rideshare"),
        M("Auto & Transport", "Service & Parts", "Transport/Service & Parts"),
        // Bills & Utilities
        M("Bills & Utilities", "Home Phone", "Bills/Internet & Phone"),
        M("Bills & Utilities", "Internet", "Bills/Internet & Phone"),
        M("Bills & Utilities", "Mobile Phone", "Bills/Internet & Phone"),
        M("Bills & Utilities", "Television", "Subscriptions"),
        M("Bills & Utilities", "Utilities", "Bills/Utilities"),
        // Business Services
        M("Business Services", "Advertising"),
        M("Business Services", "Legal"),
        M("Business Services", "Office Supplies"),
        M("Business Services", "Printing"),
        M("Business Services", "Shipping"),
        // Education
        M("Education", "Books & Supplies"),
        M("Education", "Student Loan"),
        M("Education", "Tuition"),
        // Entertainment
        M("Entertainment", "Amusement", "Entertainment"),
        M("Entertainment", "Arts"),
        M("Entertainment", "Movies & DVDs", "Entertainment"),
        M("Entertainment", "Music", "Subscriptions"),
        M("Entertainment", "Newspapers & Magazines", "Subscriptions"),
        // Fees & Charges
        M("Fees & Charges", "ATM Fee", "Fees/Bank Fees"),
        M("Fees & Charges", "Bank Fee", "Fees/Bank Fees"),
        M("Fees & Charges", "Finance Charge", "Fees/Finance Charge"),
        M("Fees & Charges", "Late Fee", "Fees/Late Fee"),
        M("Fees & Charges", "Service Fee", "Fees/Service Fee"),
        M("Fees & Charges", "Trade Commissions", "Fees/Trade Commissions"),
        // Financial
        M("Financial", "Financial Advisor"),
        M("Financial", "Life Insurance", "Bills/Insurance"),
        // Food & Dining
        M("Food & Dining", "Alcohol & Bars", "Food/Alcohol & Bars"),
        M("Food & Dining", "Coffee Shops", "Food/Dining"),
        M("Food & Dining", "Fast Food", "Food/Dining"),
        M("Food & Dining", "Groceries", "Food/Groceries"),
        M("Food & Dining", "Restaurants", "Food/Dining"),
        // Gifts & Donations
        M("Gifts & Donations", "Charity"),
        M("Gifts & Donations", "Gift"),
        // Health & Fitness
        M("Health & Fitness", "Dentist", "Health/Medical"),
        M("Health & Fitness", "Doctor", "Health/Medical"),
        M("Health & Fitness", "Eyecare", "Health/Medical"),
        M("Health & Fitness", "Gym", "Health/Gym"),
        M("Health & Fitness", "Health Insurance", "Bills/Insurance"),
        M("Health & Fitness", "Pharmacy", "Health/Medical"),
        M("Health & Fitness", "Sports", "Health/Sports"),
        // Home
        M("Home", "Furnishings", "Housing/Furnishings"),
        M("Home", "Home Improvement", "Housing/Home Maintenance"),
        M("Home", "Home Insurance", "Bills/Insurance"),
        M("Home", "Home Services", "Housing/Home Maintenance"),
        M("Home", "Home Supplies", "Housing/Home Supplies"),
        M("Home", "Lawn & Garden", "Housing/Home Maintenance"),
        M("Home", "Mortgage & Rent", "Housing/Mortgage"),
        // Income
        M("Income", "Bonus"),
        M("Income", "Interest Income", "Income/Interest"),
        M("Income", "Paycheck", "Income/Paycheck"),
        M("Income", "Reimbursement"),
        M("Income", "Rental Income"),
        M("Income", "Returned Purchase"),
        // Investments
        M("Investments", "Buy", "Investments/Trades"),
        M("Investments", "Deposit"),
        M("Investments", "Dividend & Cap Gains", "Income/Interest"),
        M("Investments", "Sell", "Investments/Trades"),
        M("Investments", "Withdrawal"),
        // Kids
        M("Kids", "Allowance", "Family/Allowance"),
        M("Kids", "Baby Supplies", "Family/Baby Supplies"),
        M("Kids", "Babysitter & Daycare", "Family/Kids & School"),
        M("Kids", "Child Support", "Family/Child Support"),
        M("Kids", "Kids Activities", "Family/Kids & School"),
        M("Kids", "Toys", "Family/Toys"),
        // Misc Expenses
        M("Misc Expenses", "Misc Expenses", "Misc Expenses"),
        // Personal Care
        M("Personal Care", "Hair"),
        M("Personal Care", "Laundry"),
        M("Personal Care", "Spa & Massage"),
        // Pets
        M("Pets", "Pet Food & Supplies"),
        M("Pets", "Pet Grooming"),
        M("Pets", "Veterinary"),
        // Shopping
        M("Shopping", "Books"),
        M("Shopping", "Clothing"),
        M("Shopping", "Electronics & Software"),
        M("Shopping", "Hobbies"),
        M("Shopping", "Sporting Goods"),
        // Taxes
        M("Taxes", "Federal Tax"),
        M("Taxes", "Local Tax"),
        M("Taxes", "Property Tax"),
        M("Taxes", "Sales Tax"),
        M("Taxes", "State Tax"),
        // Transfer
        M("Transfer", "Credit Card Payment", AutoCategorizer.TransferCategory),
        M("Transfer", "Transfer for Cash Spending", AutoCategorizer.TransferCategory),
        // Travel
        M("Travel", "Air Travel"),
        M("Travel", "Hotel"),
        M("Travel", "Rental Car & Taxi"),
        M("Travel", "Vacation"),
        // Uncategorized
        M("Uncategorized", "Cash & ATM", "Cash & ATM"),
        M("Uncategorized", "Check", "Check"),
    ];

    /// <summary>Extra FlowLedger-native paths the app itself assigns that Mint had no category for.</summary>
    private static readonly string[] Native =
    [
        "Housing/Rent", "Income/Deposits", "Other/Peer Payments", "Subscriptions", "Entertainment", "Shopping", "Travel", "Uncategorized",
    ];

    /// <summary>Distinct FlowLedger paths making up the default set.</summary>
    public static IReadOnlyList<string> Paths { get; } = Mint.Select(m => m.Path)
        .Concat(Native)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
