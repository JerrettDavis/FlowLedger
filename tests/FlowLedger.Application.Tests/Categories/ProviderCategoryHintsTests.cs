using FlowLedger.Application.Features.Categories;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Categories;

public sealed class ProviderCategoryHintsTests
{
    [Theory]
    [InlineData("Gas & Fuel", "Debit", "Transport/Fuel")]
    [InlineData("Food & Dining", "Debit", "Food/Dining")]
    [InlineData("Groceries", "Debit", "Food/Groceries")]
    [InlineData("Mortgage & Rent", "Debit", "Housing/Mortgage")]
    [InlineData("Utilities", "Debit", "Bills/Utilities")]
    [InlineData("Paycheck", "Credit", "Income/Paycheck")]
    [InlineData("Arts & Entertainment", "Debit", "Entertainment")]
    public void Specific_provider_labels_map_to_a_built_in_path(string label, string direction, string expected)
        => ProviderCategoryHints.PathFor(label, direction).Should().Be(expected);

    // The labels MX sends (its category taxonomy is Mint's; the repo's MX fixture carries "Shopping", see MxWireMockFixture).
    [Theory]
    [InlineData("Auto Insurance", "Debit", "Bills/Insurance")]
    [InlineData("Auto Payment", "Debit", "Transport/Auto Loan")]
    [InlineData("Gas", "Debit", "Transport/Fuel")]
    [InlineData("Parking", "Debit", "Transport/Parking")]
    [InlineData("Public Transportation", "Debit", "Transport/Public & Rideshare")]
    [InlineData("Service & Parts", "Debit", "Transport/Service & Parts")]
    [InlineData("Home Phone", "Debit", "Bills/Internet & Phone")]
    [InlineData("Mobile Phone", "Debit", "Bills/Internet & Phone")]
    [InlineData("Television", "Debit", "Subscriptions")]
    [InlineData("Tuition", "Debit", "Education/Tuition")]
    [InlineData("Student Loan", "Debit", "Education/Student Loan")]
    [InlineData("Books & Supplies", "Debit", "Education/Books & Supplies")]
    [InlineData("Amusement", "Debit", "Entertainment")]
    [InlineData("Movies & DVDs", "Debit", "Entertainment")]
    [InlineData("Newspapers & Magazines", "Debit", "Subscriptions")]
    [InlineData("ATM Fee", "Debit", "Fees/Bank Fees")]
    [InlineData("Finance Charge", "Debit", "Fees/Finance Charge")]
    [InlineData("Late Fee", "Debit", "Fees/Late Fee")]
    [InlineData("Service Fee", "Debit", "Fees/Service Fee")]
    [InlineData("Trade Commissions", "Debit", "Fees/Trade Commissions")]
    [InlineData("Alcohol & Bars", "Debit", "Food/Alcohol & Bars")]
    [InlineData("Coffee Shops", "Debit", "Food/Dining")]
    [InlineData("Fast Food", "Debit", "Food/Dining")]
    [InlineData("Restaurants", "Debit", "Food/Dining")]
    [InlineData("Charity", "Debit", "Gifts & Donations/Charity")]
    [InlineData("Gift", "Debit", "Gifts & Donations/Gift")]
    [InlineData("Dentist", "Debit", "Health/Medical")]
    [InlineData("Eyecare", "Debit", "Health/Medical")]
    [InlineData("Gym", "Debit", "Health/Gym")]
    [InlineData("Health Insurance", "Debit", "Bills/Insurance")]
    [InlineData("Pharmacy", "Debit", "Health/Medical")]
    [InlineData("Sports", "Debit", "Health/Sports")]
    [InlineData("Furnishings", "Debit", "Housing/Furnishings")]
    [InlineData("Home Improvement", "Debit", "Housing/Home Maintenance")]
    [InlineData("Home Services", "Debit", "Housing/Home Maintenance")]
    [InlineData("Home Supplies", "Debit", "Housing/Home Supplies")]
    [InlineData("Lawn & Garden", "Debit", "Housing/Home Maintenance")]
    [InlineData("Bonus", "Credit", "Income/Bonus")]
    [InlineData("Interest Income", "Credit", "Income/Interest")]
    [InlineData("Reimbursement", "Credit", "Income/Reimbursement")]
    [InlineData("Rental Income", "Credit", "Income/Rental Income")]
    [InlineData("Returned Purchase", "Credit", "Income/Returned Purchase")]
    [InlineData("Dividend & Cap Gains", "Credit", "Income/Interest")]
    [InlineData("Buy", "Debit", "Investments/Trades")]
    [InlineData("Allowance", "Debit", "Family/Allowance")]
    [InlineData("Baby Supplies", "Debit", "Family/Baby Supplies")]
    [InlineData("Babysitter & Daycare", "Debit", "Family/Kids & School")]
    [InlineData("Child Support", "Debit", "Family/Child Support")]
    [InlineData("Toys", "Debit", "Family/Toys")]
    [InlineData("Hair", "Debit", "Personal Care/Hair")]
    [InlineData("Pet Food & Supplies", "Debit", "Pets/Pet Food & Supplies")]
    [InlineData("Veterinary", "Debit", "Pets/Veterinary")]
    [InlineData("Clothing", "Debit", "Shopping/Clothing")]
    [InlineData("Electronics & Software", "Debit", "Shopping/Electronics & Software")]
    [InlineData("Federal Tax", "Debit", "Taxes/Federal Tax")]
    [InlineData("Property Tax", "Debit", "Taxes/Property Tax")]
    [InlineData("Credit Card Payment", "Debit", "Transfer")]
    [InlineData("Transfer for Cash Spending", "Debit", "Transfer")]
    [InlineData("Air Travel", "Debit", "Travel")]
    [InlineData("Rental Car & Taxi", "Debit", "Travel")]
    public void MX_category_labels_map_to_the_matching_default_category(string label, string direction, string expected)
        => ProviderCategoryHints.PathFor(label, direction).Should().Be(expected);

    [Fact]
    public void Every_hint_lands_on_a_category_the_default_set_already_contains()
    {
        // Every label the repo knows MX (Mint-compatible) can send: a hint must never invent a stray category.
        var labels = DefaultCategories.Mint.SelectMany(m => new[] { m.MintCategory, m.MintGroup }).Distinct();
        foreach (var label in labels)
        {
            foreach (var direction in new[] { "Debit", "Credit" })
            {
                if (ProviderCategoryHints.PathFor(label, direction) is { } path)
                {
                    DefaultCategories.Paths.Should().Contain(path, because: $"\"{label}\" ({direction}) hints at it");
                }
            }
        }
    }

    [Theory]
    [InlineData("Shopping")]
    [InlineData("Gifts & Donations")]
    [InlineData("Income")]
    [InlineData("Taxes")]
    [InlineData("Uncategorized")]
    [InlineData("Misc Expenses")]
    [InlineData("Deposit")]
    [InlineData("Withdrawal")]
    [InlineData("Check")]
    [InlineData("Business Services")]
    public void MX_top_level_or_catch_all_labels_stay_silent(string label)
        => ProviderCategoryHints.PathFor(label, "Debit").Should().BeNull();

    [Fact]
    public void Income_style_labels_do_not_hint_on_debits()
    {
        ProviderCategoryHints.PathFor("Bonus", "Debit").Should().BeNull();
        ProviderCategoryHints.PathFor("Returned Purchase", "Debit").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Uncategorized")]
    [InlineData("Shopping")]
    [InlineData("Auto Parts")]
    [InlineData("Other")]
    public void Vague_or_unknown_labels_give_no_hint(string? label)
        => ProviderCategoryHints.PathFor(label, "Debit").Should().BeNull();

    [Fact]
    public void Income_labels_only_apply_to_credits()
        => ProviderCategoryHints.PathFor("Paycheck", "Debit").Should().BeNull();
}
