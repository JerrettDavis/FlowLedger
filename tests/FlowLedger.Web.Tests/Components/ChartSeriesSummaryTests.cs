using System.Globalization;
using FlowLedger.Web.Shared;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Components;

public sealed class ChartSeriesSummaryTests
{
    [Fact]
    public void Series_summary_states_start_end_high_and_low_per_series()
    {
        var text = ChartSummary.Series("Growth", ["Y0", "Y1", "Y2"],
            [("Current", new[] { 100d, 150d, 120d }), ("Change", new[] { 100d, 200d, 300d })], v => v.ToString("0", CultureInfo.InvariantCulture));

        text.Should().Contain("Current goes from 100 at Y0 to 120 at Y2 (high 150, low 100)")
            .And.Contain("Change goes from 100 at Y0 to 300 at Y2 (high 300, low 100)");
    }

    [Fact]
    public void Series_summary_without_data_says_so()
    {
        ChartSummary.Series("Growth", [], [("A", Array.Empty<double>())], v => v.ToString("0", CultureInfo.InvariantCulture)).Should().Be("Growth: no data.");
    }

    [Fact]
    public void Every_chart_in_the_app_sits_inside_a_ChartFigure()
    {
        foreach (var file in RepoFiles.Razor().Where(f => !f.EndsWith("ChartFigure.razor")))
        {
            var text = File.ReadAllText(file);
            var charts = System.Text.RegularExpressions.Regex.Matches(text, "<MudChart |SvgChart\\.Build").Count;
            var figures = System.Text.RegularExpressions.Regex.Matches(text, "<ChartFigure ").Count;
            if (charts > 0)
            {
                figures.Should().BeGreaterThanOrEqualTo(charts, $"{Path.GetFileName(file)} has charts without a text alternative");
            }
        }
    }
}
