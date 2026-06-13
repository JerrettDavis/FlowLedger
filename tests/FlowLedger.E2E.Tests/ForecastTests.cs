namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Forecasts page.
/// Verifies page load, heading, SVG chart, Run Forecast button, and nav link.
///
/// Data expectation: after seeding, the aggregate SVG chart must be visible.
/// "No forecast data" is treated as a failure — it indicates the forecast API silently failed.
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
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Forecasts");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Forecasts: main heading is visible")]
    public async Task Forecasts_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Forecasts" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Forecasts: 'Run Forecast' button is present")]
    public async Task Forecasts_RunForecastButtonIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // aria-label="Run forecast" from Forecasts.razor
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Run forecast" });
        (await btn.CountAsync()).Should().BeGreaterThan(0);
        (await btn.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    /// <summary>
    /// Asserts that the aggregate SVG chart renders with real forecast data.
    ///
    /// "No forecast data" is NOT accepted as success — it appears when the forecast API
    /// silently fails (returns a non-JSON response, 500, or empty series).  After seeding,
    /// the chart must render.
    /// </summary>
    [Fact(DisplayName = "Forecasts: aggregate SVG chart renders (no-data fallback is a failure)")]
    public async Task Forecasts_AggregateSvgChartRendersWithData()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        // role="img" aria-label="Aggregate balance projection chart" from Forecasts.razor.
        // Only rendered when AggregateSeries.Count > 0; otherwise "No forecast data" shown.
        var chart = Page!.GetByRole(AriaRole.Img, new() { Name = "Aggregate balance projection chart" });

        await chart.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        (await chart.CountAsync()).Should().BeGreaterThan(0,
            "the Aggregate Balance Projection SVG chart must be visible after data is seeded. " +
            "If 'No forecast data' is shown instead, the forecast API likely returned an error " +
            "or non-JSON response that was silently swallowed.");

        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Forecasts: nav link is present")]
    public async Task Forecasts_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Forecasts" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }
}
