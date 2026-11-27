using System.Globalization;
using FlowLedger.Web.Shared;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Shared;

public sealed class FormatCountRatioTests
{
    private static T With<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Plain(string value) => Regex().Replace(value, " ");

    private static System.Text.RegularExpressions.Regex Regex() => new(@"\s");

    [Fact]
    public void Count_groups_digits_for_the_display_culture()
    {
        With("en-US", () => Format.Count(1234567)).Should().Be("1,234,567");
        With("de-DE", () => Format.Count(1234567)).Should().Be("1.234.567");
    }

    [Fact]
    public void Number_drops_decimals_only_for_whole_values()
    {
        With("en-US", () => Format.Number(1500m)).Should().Be("1,500");
        With("en-US", () => Format.Number(1500.5m)).Should().Be("1,500.50");
    }

    [Fact]
    public void Ratio_is_a_whole_percentage_in_the_display_culture()
    {
        With("en-US", () => Format.Ratio(0.256m)).Should().Be("26%");
        With("en-US", () => Format.Ratio(0.5)).Should().Be("50%");
        Plain(With("de-DE", () => Format.Ratio(0.256m))).Should().Be("26 %");
    }
}
