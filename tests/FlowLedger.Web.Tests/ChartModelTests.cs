using System.Text.Json;
using FlowLedger.TestSupport.Builders;
using FlowLedger.Web.Shared;
using FluentAssertions;

namespace FlowLedger.Web.Tests;

public sealed class ChartModelTests
{
    private static List<object?> Row(params object?[] cells)
        => cells.Select(c => (object?)JsonSerializer.SerializeToElement(c)).ToList();

    private static QueryResult Result(string[] names, string[] types, params List<object?>[] rows)
        => QueryResultBuilder.A()
            .WithColumns(names.Zip(types, (n, t) => QueryColumnBuilder.A().WithName(n).WithType(t).Build()).ToList())
            .WithRows(rows.Select(r => (IReadOnlyList<object?>)r).ToList())
            .WithElapsedMs(0)
            .Build();

    [Fact]
    public void Multiple_value_columns_become_series()
    {
        var r = Result(["month", "income", "spending"], ["string", "number", "number"],
            Row("2026-01", 10, 4), Row("2026-02", 12, 6));
        var m = ChartModel.Build(r, VizSpecBuilder.A().WithKind("line").WithX("month").WithY(["income", "spending"]).Build());

        m.Categories.Should().Equal("2026-01", "2026-02");
        m.Series.Select(s => s.Name).Should().Equal("income", "spending");
        m.Series[1].Values.Should().Equal(4d, 6d);
        m.XIsTime.Should().BeTrue();
    }

    [Fact]
    public void SeriesBy_pivots_long_rows_and_leaves_gaps_as_null()
    {
        var r = Result(["month", "category", "total"], ["string", "string", "number"],
            Row("2026-01", "Food", 5), Row("2026-01", "Rent", 9), Row("2026-02", "Food", 7));
        var m = ChartModel.Build(r, VizSpecBuilder.A().WithKind("line").WithX("month").WithY(["total"]).WithSeriesBy("category").Build());

        m.Series.Select(s => s.Name).Should().BeEquivalentTo("Food", "Rent");
        m.Series.Single(s => s.Name == "Rent").Values.Should().Equal(9d, null);
    }

    [Fact]
    public void More_than_eight_series_fold_into_other()
    {
        var rows = Enumerable.Range(0, 12).Select(i => Row("2026-01", $"c{i}", i + 1)).ToArray();
        var r = Result(["month", "category", "total"], ["string", "string", "number"], rows);
        var m = ChartModel.Build(r, VizSpecBuilder.A().WithX("month").WithY(["total"]).WithSeriesBy("category").Build());

        m.Series.Should().HaveCount(ChartModel.MaxSeries);
        m.Series[^1].Name.Should().Be("Other");
        m.Series.Sum(s => s.Values[0]!.Value).Should().Be(Enumerable.Range(1, 12).Sum());
    }

    [Fact]
    public void Empty_or_non_numeric_results_report_a_problem_instead_of_throwing()
    {
        ChartModel.Build(Result(["a"], ["string"]), VizSpecBuilder.A().Build()).Problem.Should().NotBeNull();
        ChartModel.Build(Result(["a"], ["string"], Row("x")), VizSpecBuilder.A().Build()).Problem.Should().Contain("numeric");
    }

    [Theory]
    [InlineData("column")]
    [InlineData("bar")]
    [InlineData("line")]
    [InlineData("area")]
    [InlineData("pie")]
    public void Every_chart_kind_renders_svg(string kind)
    {
        var r = Result(["name", "v"], ["string", "number"], Row("a", 3), Row("b", -2), Row("c", 5));
        var svg = QueryChartSvg.Render(ChartModel.Build(r, VizSpecBuilder.A().WithKind(kind).WithX("name").WithY(["v"]).Build()), kind, stacked: false).Value;
        svg.Should().Contain("<svg");
    }
}
