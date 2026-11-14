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
    // Real-world bank descriptors
    [InlineData("ONLINE PAYMENT, THANK YOU", "Credit", "Transfer")]
    [InlineData("CITI CARD ONLINE PAYMENT JERRETT D DAVIS", "Debit", "Transfer")]
    [InlineData("CHASE CREDIT CRD EPAY JERRETT D DAVIS", "Debit", "Transfer")]
    [InlineData("Credit Karma     Transfer Jerrett Davis", "Debit", "Transfer")]
    [InlineData("DIVIDEND REINVEST - SCHWAB INTERMEDIATE TERM", "Debit", "Investments/Trades")]
    [InlineData("DIVIDEND PAYMENT - LORD ABBETT SHORT DURATION", "Credit", "Income/Interest")]
    [InlineData("SELL - ISHARES MSCI US SMALL CAP EQUITY", "Credit", "Investments/Trades")]
    [InlineData("QUIKTRIP CORP    PREAUTHPMT 12880 E 51st St", "Debit", "Transport/Fuel")]
    [InlineData("QT 31 DDA PIN POS PUR CD4506 TULSA OK", "Debit", "Transport/Fuel")]
    [InlineData("QTR CORPORATION PAYROLLQTC JERRETT D DAVIS", "Credit", "Income/Paycheck")]
    [InlineData("ROCKET MORTGAGE  LOAN JERRETT *DAVIS", "Debit", "Housing/Mortgage")]
    [InlineData("HMF HMFUSA.com JERRETT DAVIS", "Debit", "Transport/Auto Loan")]
    [InlineData("AMER ELECT PWR CPPWDRAWAL DAVIS, JERRETT", "Debit", "Bills/Utilities")]
    [InlineData("OK NATURAL GAS UTIL PAYMT JERRETT DAVIS", "Debit", "Bills/Utilities")]
    [InlineData("ANTHROPIC* CLAUDE SUB SAN FRANCISCO CA", "Debit", "Subscriptions")]
    [InlineData("OPENAI *CHATGPT SUBSCR SAN FRANCISCO CA", "Debit", "Subscriptions")]
    [InlineData("STATE FARM RO 27 CPC-CLIENT JERRETT DAVIS", "Debit", "Bills/Insurance")]
    [InlineData("ATM USAGE FEE", "Debit", "Fees/Bank Fees")]
    [InlineData("TACO CASA 12 TULSA OK", "Debit", "Food/Dining")]
    [InlineData("THE HOME DEPOT 1234", "Debit", "Housing/Home Maintenance")]
    [InlineData("MUSTANG MOWING TULSA OK", "Debit", "Housing/Home Maintenance")]
    [InlineData("AMAZON MKTPL*AB12", "Debit", "Shopping")]
    [InlineData("SAMS CLUB 6389", "Debit", "Food/Groceries")]
    [InlineData("CHK DEPOSIT MOBILE", "Credit", "Income/Deposits")]
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
