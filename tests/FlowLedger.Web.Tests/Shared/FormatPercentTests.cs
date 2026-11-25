using FlowLedger.Web.Shared;
using Xunit;

namespace FlowLedger.Web.Tests.Shared;

public sealed class FormatPercentTests
{
    [Theory]
    [InlineData(50, 1000, "+5%")]
    [InlineData(-25, 1000, "−2.5%")]
    [InlineData(150, 1000, "+15%")]
    [InlineData(0, 1000, "+0%")]
    [InlineData(100, -1000, "+10%")]
    [InlineData(10, 0, "")]
    public void Percent_formats_relative_to_baseline(decimal change, decimal baseline, string expected) =>
        Assert.Equal(expected, Format.Percent(change, baseline));

    [Fact]
    public void SignedWithPercent_appends_percentage_when_computable() =>
        Assert.Equal("+$50.00 (+5%)", Format.SignedWithPercent(50m, 1000m));

    [Fact]
    public void SignedWithPercent_omits_percentage_for_zero_baseline() =>
        Assert.Equal("+$50.00", Format.SignedWithPercent(50m, 0m));
}
