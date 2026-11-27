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
    [E2EFact(DisplayName = "Forecasts: page title contains 'Forecasts'")]
    public async Task Forecasts_PageTitleContainsForecasts()
    {
        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Forecasts");
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Forecasts: main heading is visible")]
    public async Task Forecasts_MainHeadingIsVisible()
    {
        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Forecasts" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Forecasts: 'Run Forecast' button is present")]
    public async Task Forecasts_RunForecastButtonIsPresent()
    {
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
    [E2EFact(DisplayName = "Forecasts: aggregate SVG chart renders (no-data fallback is a failure)")]
    public async Task Forecasts_AggregateSvgChartRendersWithData()
    {
        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        // role="img" named by the ChartFigure caption "Aggregate balance projection" (Forecasts.razor).
        // Only rendered when AggregateSeries.Count > 0; otherwise "No forecast data" shown.
        var chart = Page!.GetByRole(AriaRole.Img, new() { Name = "Aggregate balance projection" });

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

    [E2EFact(DisplayName = "Forecasts: nav link is present")]
    public async Task Forecasts_NavLinkIsPresent()
    {
        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Forecasts" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }
}
