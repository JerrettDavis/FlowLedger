namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E tests for the Dashboard page.
/// Verifies that the application loads, the dashboard renders, and core UI elements are present.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class DashboardTests : E2ETestBase
{
    [Fact(DisplayName = "Dashboard loads and displays title")]
    public async Task DashboardLoads_DisplaysPageTitle()
    {
        // Arrange & Act
        await NavigateAsync("/");

        // Assert
        var title = await Page!.TitleAsync();
        title.Should().Contain("Dashboard");
    }

    [Fact(DisplayName = "Dashboard renders main heading")]
    public async Task Dashboard_RendersMainHeading()
    {
        // Arrange & Act
        await NavigateAsync("/");

        // Assert
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await heading.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Navigation menu is present")]
    public async Task Dashboard_NavigationMenuIsPresent()
    {
        // Arrange & Act
        await NavigateAsync("/");

        // Assert
        var navMenu = Page!.GetByRole(AriaRole.Navigation, new() { Name = "Main navigation menu" });
        (await navMenu.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await navMenu.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Dashboard contains summary cards")]
    public async Task Dashboard_ContainsSummaryCards()
    {
        // Arrange & Act
        await NavigateAsync("/");
        await WaitForLoadAsync();

        // Assert - Check for summary card labels
        (await Page!.GetByText("Total Balance").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Net Worth").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Forecast Low").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Overdraft Risk").CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "Balance Projection chart is rendered")]
    public async Task Dashboard_BalanceProjectionChartRendered()
    {
        // Arrange & Act
        await NavigateAsync("/");
        await WaitForLoadAsync();

        // Assert - Check for chart container and heading
        (await Page!.GetByText("Balance Projection").CountAsync()).Should().BeGreaterThan(0);

        // SVG should be present (or "no forecast data" message)
        var chartArea = Page!.GetByRole(AriaRole.Img, new() { Name = "Balance projection chart" });
        (await chartArea.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await chartArea.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Upcoming Flows section is present")]
    public async Task Dashboard_UpcomingFlowsSectionPresent()
    {
        // Arrange & Act
        await NavigateAsync("/");
        await WaitForLoadAsync();

        // Assert
        (await Page!.GetByText("Upcoming Flows").CountAsync()).Should().BeGreaterThan(0);
    }
}
