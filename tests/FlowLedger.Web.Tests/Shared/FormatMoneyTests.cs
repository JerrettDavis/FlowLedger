using System.Globalization;
using FlowLedger.Web.Shared;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Shared;

public sealed class FormatMoneyTests
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Eur_in_de_DE_uses_local_separators_and_the_euro_sign()
    {
        System.Text.RegularExpressions.Regex.Replace(Format.Money(1234.5m, "EUR", De), @"\s", " ").Should().Be("1.234,50 €");
    }

    [Fact]
    public void Eur_in_en_US_shows_euro_not_dollar()
    {
        var text = Format.Money(1234.5m, "EUR", Us);

        text.Should().Be("€1,234.50");
        text.Should().NotContain("$");
    }

    [Theory]
    [InlineData("USD", "$1,234.50")]
    [InlineData("GBP", "£1,234.50")]
    [InlineData("usd", "$1,234.50")]
    public void Known_currencies_use_their_symbol(string code, string expected)
    {
        Format.Money(1234.5m, code, Us).Should().Be(expected);
    }

    [Fact]
    public void Unknown_currency_falls_back_to_the_iso_code_never_a_dollar_sign()
    {
        var text = Format.Money(5m, "XYZ", Us);

        text.Should().Contain("XYZ").And.NotContain("$");
    }

    [Fact]
    public void Jpy_has_no_decimals()
    {
        Format.Money(1234m, "JPY", Us).Should().Be("¥1,234");
    }

    [Fact]
    public void Missing_currency_keeps_the_legacy_usd_behaviour()
    {
        Format.Money(1234.5m, null, Us).Should().Be(Format.Money(1234.5m));
    }

    [Fact]
    public void Negative_amounts_keep_a_visible_minus()
    {
        Format.Money(-10m, "EUR", Us).Should().Contain("10.00").And.Match("*-*");
    }

    [Fact]
    public void Accounts_page_formats_balances_with_the_account_currency()
    {
        RepoFiles.Read("Components/Pages/Accounts.razor").Should().Contain("Format.Money(acct.BalanceAmount, acct.BalanceCurrency)");
    }
}
