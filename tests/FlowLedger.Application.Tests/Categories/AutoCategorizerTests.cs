using FluentAssertions;
using FlowLedger.Application.Features.Categories;

namespace FlowLedger.Application.Tests.Categories;

public sealed class AutoCategorizerTests
{
    [Theory]
    [InlineData("Bluejay Grocery Co.", "Debit", "Food/Groceries")]
    [InlineData("The Copper Kettle Bistro", "Debit", "Food/Dining")]
    [InlineData("Ironwood Pizza Kitchen", "Debit", "Food/Dining")]
    [InlineData("QuickFuel Stations", "Debit", "Transport/Fuel")]
    [InlineData("Metro Transit Authority", "Debit", "Transport/Public & Rideshare")]
    [InlineData("Apex Rideshare", "Debit", "Transport/Public & Rideshare")]
    [InlineData("CityPark Garages", "Debit", "Transport/Parking")]
    [InlineData("Ridgeline Insurance", "Debit", "Bills/Insurance")]
    [InlineData("BluePeak Health", "Debit", "Health/Medical")]
    [InlineData("Clearview Medical Group", "Debit", "Health/Medical")]
    [InlineData("Lumen Utilities", "Debit", "Bills/Utilities")]
    [InlineData("ClearWave Internet", "Debit", "Bills/Internet & Phone")]
    [InlineData("StreamFlix", "Debit", "Subscriptions")]
    [InlineData("SoundWave Music", "Debit", "Subscriptions")]
    [InlineData("CloudVault Storage", "Debit", "Subscriptions")]
    [InlineData("Lakeview Mortgage Services", "Debit", "Housing/Mortgage")]
    [InlineData("MarketNest Online", "Debit", "Shopping")]
    [InlineData("TechDen Electronics", "Debit", "Shopping")]
    [InlineData("INTEREST CREDIT", "Credit", "Income/Interest")]
    [InlineData("Internal Transfer", "Debit", "Transfer")]
    public void Common_merchants_get_a_sensible_category(string merchant, string direction, string expected)
    {
        AutoCategorizer.Categorize(merchant, merchant, direction).Should().Be(expected);
    }

    [Fact]
    public void Direction_specific_rules_do_not_fire_on_the_wrong_side()
    {
        AutoCategorizer.Categorize("Interest charge", null, "Debit").Should().BeNull();
    }

    [Fact]
    public void Unknown_merchants_stay_uncategorised()
    {
        AutoCategorizer.Categorize("Zzyzx Holdings", "Zzyzx Holdings", "Debit").Should().BeNull();
        AutoCategorizer.Categorize(null, null, "Debit").Should().BeNull();
    }

    [Fact]
    public void Merchant_key_prefers_merchant_name_and_normalises_case()
    {
        AutoCategorizer.MerchantKey(" Cafe Roma ", "POS 123").Should().Be("CAFE ROMA");
        AutoCategorizer.MerchantKey(null, "pos 123").Should().Be("POS 123");
    }
}
