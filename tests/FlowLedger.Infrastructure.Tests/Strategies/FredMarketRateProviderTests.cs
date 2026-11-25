using FlowLedger.Infrastructure.Strategies;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Strategies;

public class FredMarketRateProviderTests
{
    [Fact]
    public void ParseLatest_takes_last_numeric_row_and_skips_missing_markers()
    {
        var csv = "observation_date,DGS1\n2026-11-18,3.90\n2026-11-19,3.95\n2026-11-20,.\n";
        FredMarketRateProvider.ParseLatest(csv).Should().Be((new DateOnly(2026, 11, 19), 3.95m));
    }

    [Theory]
    [InlineData("")]
    [InlineData("observation_date,X\n2026-01-01,.\n")]
    [InlineData("<html>blocked</html>")]
    [InlineData("observation_date,X\n2026-01-01,5000\n")] // implausible rate rejected
    public void ParseLatest_returns_null_for_unusable_payloads(string csv)
    {
        FredMarketRateProvider.ParseLatest(csv).Should().BeNull();
    }
}
