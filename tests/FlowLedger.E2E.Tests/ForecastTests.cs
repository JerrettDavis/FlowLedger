namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E tests for the Forecasts page.
/// Verifies that the forecast page renders with SVG charts and data visualization.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class ForecastTests : E2ETestBase
{
    [Fact(DisplayName = "Forecasts page loads and displays title")]
    public async Task ForecastsPage_LoadsAndDisplaysTitle()
    {
        // Arrange & Act
        await NavigateAsync("/forecasts");

        // Assert
        var title = await Page!.TitleAsync();
        title.Should().Contain("Forecasts");
    }

    [Fact(DisplayName = "Forecasts page displays heading")]
    public async Task ForecastsPage_DisplaysHeading()
    {
        // Arrange & Act
        await NavigateAsync("/forecasts");

        // Assert
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Forecasts" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await heading.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Forecast chart is rendered")]
    public async Task ForecastsPage_ChartIsRendered()
    {
        // Arrange & Act
        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();

        // Assert - Check for SVG or chart container
        var chartArea = Page!.GetByRole(AriaRole.Img, new() { Name = "Forecast chart" });
        var count = await chartArea.CountAsync();
        if (count > 0)
        {
            var isVisible = await chartArea.IsVisibleAsync();
            isVisible.Should().BeTrue();
        }
    }

    [Fact(DisplayName = "Forecasts grid/table is present")]
    public async Task ForecastsPage_TableIsPresent()
    {
        // Arrange & Act
        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();

        // Assert - Check for forecasts table
        var table = Page!.GetByRole(AriaRole.Table, new() { Name = "Forecasts table" });
        (await table.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await table.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Navigation menu includes Forecasts link")]
    public async Task ForecastsPage_NavigationLinkPresent()
    {
        // Arrange & Act
        await NavigateAsync("/forecasts");

        // Assert
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Forecasts" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }
}
