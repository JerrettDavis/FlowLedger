namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Forecasts page.
/// Verifies page load, heading, SVG chart, Run Forecast button, and nav link.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class ForecastTests : E2ETestBase
{
    [Fact(DisplayName = "Forecasts: page title contains 'Forecasts'")]
    public async Task Forecasts_PageTitleContainsForecasts()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        var title = await Page!.TitleAsync();
        title.Should().Contain("Forecasts");
    }

    [Fact(DisplayName = "Forecasts: main heading is visible")]
    public async Task Forecasts_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Forecasts" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Forecasts: 'Run Forecast' button is present")]
    public async Task Forecasts_RunForecastButtonIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        // aria-label="Run forecast" from Forecasts.razor
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Run forecast" });
        (await btn.CountAsync()).Should().BeGreaterThan(0);
        (await btn.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Forecasts: aggregate SVG chart or no-data message is present")]
    public async Task Forecasts_SvgChartOrNoDataMessagePresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        // role="img" aria-label="Aggregate balance projection chart" from Forecasts.razor
        // Only rendered when AggregateSeries.Count > 0; otherwise "No forecast data" shown
        var chart = Page!.GetByRole(AriaRole.Img, new() { Name = "Aggregate balance projection chart" });
        var noData = Page!.GetByText("No forecast data");
        var chartCount = await chart.CountAsync();
        var noDataCount = await noData.CountAsync();
        (chartCount + noDataCount).Should().BeGreaterThan(0,
            "expected either the aggregate SVG chart or the 'no forecast data' fallback message");
    }

    [Fact(DisplayName = "Forecasts: nav link is present")]
    public async Task Forecasts_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Forecasts" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }
}
