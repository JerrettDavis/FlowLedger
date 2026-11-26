using Bunit;
using FlowLedger.Web.Components.Shared;
using FlowLedger.Web.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace FlowLedger.Web.Tests.Components;

public sealed class ChartAccessibilityTests : BunitContext
{
    private static string Money(decimal v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Figure_has_a_name_and_a_non_empty_description_wired_by_id()
    {
        var cut = Render<ChartFigure>(p => p
            .Add(x => x.Title, "Spending by category")
            .Add(x => x.Summary, "Groceries 120.00, Rent 900.00")
            .Add(x => x.ChildContent, (RenderFragment)(b => b.AddMarkupContent(0, "<svg></svg>"))));

        var figure = cut.Find("figure");
        var caption = cut.Find("figcaption");
        caption.TextContent.Should().Be("Spending by category");
        figure.GetAttribute("aria-labelledby").Should().Be(caption.Id);

        var describedBy = figure.GetAttribute("aria-describedby");
        describedBy.Should().NotBeNullOrEmpty();
        cut.Find($"#{describedBy}").TextContent.Should().Contain("Rent 900.00");

        var img = cut.Find("[role=img]");
        img.GetAttribute("aria-labelledby").Should().Be(caption.Id);
        img.GetAttribute("aria-describedby").Should().Be(describedBy);
    }

    [Fact]
    public void Figure_offers_the_data_as_a_table_when_rows_are_supplied()
    {
        var cut = Render<ChartFigure>(p => p
            .Add(x => x.Title, "Income vs spending")
            .Add(x => x.Summary, "summary")
            .Add(x => x.Rows, [new ChartRow("2026-01", "100.00"), new ChartRow("2026-02", "90.00")]));

        cut.Find("details summary").TextContent.Should().Be("View data");
        cut.FindAll("details tbody tr").Should().HaveCount(2);
        cut.FindAll("details tbody tr")[1].TextContent.Should().Contain("2026-02").And.Contain("90.00");
    }

    [Fact]
    public void Figure_without_rows_has_no_data_toggle()
    {
        var cut = Render<ChartFigure>(p => p.Add(x => x.Title, "t").Add(x => x.Summary, "s"));

        cut.FindAll("details").Should().BeEmpty();
    }

    [Fact]
    public void Two_figures_get_distinct_ids()
    {
        var a = Render<ChartFigure>(p => p.Add(x => x.Title, "a").Add(x => x.Summary, "s"));
        var b = Render<ChartFigure>(p => p.Add(x => x.Title, "b").Add(x => x.Summary, "s"));

        a.Find("figcaption").Id.Should().NotBe(b.Find("figcaption").Id);
    }

    [Fact]
    public void AllocationLegend_lists_every_group_with_its_percent()
    {
        var cut = Render<AllocationLegend>(p => p.Add(x => x.Items,
            [new AllocationItem("Cash", "cash", 62.5), new AllocationItem("Loans", "loan", 37.5)]));

        var items = cut.FindAll("ul li");
        items.Should().HaveCount(2);
        items[0].TextContent.Should().Contain("Cash").And.Contain("62.5%");
        items[1].TextContent.Should().Contain("Loans").And.Contain("37.5%");
    }

    [Fact]
    public void Summary_names_the_largest_categories_and_the_remainder()
    {
        var rows = new[] { ("A", 10m), ("B", 50m), ("C", 30m), ("D", 5m) };

        var text = ChartSummary.Categories("Spending", rows, Money, top: 2);

        text.Should().StartWith("Spending:").And.Contain("B 50.00").And.Contain("C 30.00");
        text.Should().Contain("2 more");
        text.Should().NotContain("A 10.00");
    }

    [Fact]
    public void Summary_for_empty_data_says_so()
    {
        ChartSummary.Categories("Spending", [], Money).Should().Be("Spending: no data.");
        ChartSummary.Trend("Net worth", [], Money).Should().Be("Net worth: no data.");
    }

    [Fact]
    public void Trend_summary_reports_start_end_change_and_range()
    {
        var pts = new[] { ("Jan", 100m), ("Feb", 160m), ("Mar", 130m) };

        var text = ChartSummary.Trend("Net worth", pts, Money);

        text.Should().Contain("Jan 100.00").And.Contain("Mar 130.00").And.Contain("up 30.00")
            .And.Contain("high 160.00").And.Contain("low 100.00");
    }

    [Fact]
    public void Progress_value_text_reads_as_a_sentence()
    {
        ChartSummary.ProgressText("Groceries", 82.46, "of budget used").Should().Be("Groceries: 82% of budget used");
    }
}
