using System.Globalization;
using FlowLedger.Web.Services;
using FlowLedger.Web.Shared;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Shared;

public sealed class DisplaySettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("ZZZ|xx-XX")]
    public void Missing_or_invalid_values_fall_back_to_usd_en_us(string? value) =>
        DisplaySettings.Parse(value).Should().Be(DisplaySettings.Default);

    [Fact]
    public void Cookie_value_round_trips()
    {
        var settings = new DisplaySettings("EUR", "de-DE");

        DisplaySettings.Parse(settings.ToCookieValue()).Should().Be(settings);
    }

    [Fact]
    public void Currency_and_locale_parse_independently()
    {
        DisplaySettings.Parse("eur|nope").Should().Be(new DisplaySettings("EUR", "en-US"));
        DisplaySettings.Parse("XXX|de-DE").Should().Be(new DisplaySettings("USD", "de-DE"));
    }

    [Fact]
    public void Default_formats_as_us_dollars_without_conversion()
    {
        WithCulture(DisplaySettings.Default.ToCulture(), () =>
        {
            Format.Money(1234.5m).Should().Be("$1,234.50");
            Format.Signed(-5m).Should().Be("−$5.00");
        });
    }

    [Fact]
    public void Euro_in_german_locale_uses_local_separators_but_keeps_the_amount()
    {
        WithCulture(new DisplaySettings("EUR", "de-DE").ToCulture(), () =>
            System.Text.RegularExpressions.Regex.Replace(Format.Money(1234.5m), @"\s", " ").Should().Be("1.234,50 €"));
    }

    [Fact]
    public void Yen_has_no_decimals()
    {
        WithCulture(new DisplaySettings("JPY", "ja-JP").ToCulture(), () => Format.Money(1234m).Should().Be("¥1,234"));
    }

    [Fact]
    public void Service_raises_changed_only_when_settings_differ()
    {
        var service = new DisplaySettingsService();
        var raised = 0;
        service.Changed += () => raised++;

        service.Set(DisplaySettings.Default);
        service.Set(new DisplaySettings("GBP", "en-GB"));

        raised.Should().Be(1);
        service.Current.Currency.Should().Be("GBP");
    }

    [Fact]
    public void Service_applies_its_culture_to_the_current_thread()
    {
        var service = new DisplaySettingsService();
        service.Set(new DisplaySettings("GBP", "en-GB"));

        WithCulture(CultureInfo.CurrentCulture, () =>
        {
            service.ApplyToCurrentThread();

            Format.Money(10m).Should().Be("£10.00");
        });
    }

    private static void WithCulture(CultureInfo culture, Action act)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            act();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
