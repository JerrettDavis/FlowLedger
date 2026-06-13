namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Dashboard page.
/// Verifies app load, title, nav, summary cards, SVG forecast chart, and upcoming-flows section.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class DashboardTests : E2ETestBase
{
    [Fact(DisplayName = "Dashboard: page title contains 'Dashboard'")]
    public async Task Dashboard_PageTitleContainsDashboard()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        var title = await Page!.TitleAsync();
        title.Should().Contain("Dashboard");
    }

    [Fact(DisplayName = "Dashboard: main heading is visible")]
    public async Task Dashboard_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Dashboard: navigation menu is present")]
    public async Task Dashboard_NavigationMenuIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        // NavMenu renders with aria-label="Main navigation menu" (NavMenu.razor)
        var nav = Page!.GetByRole(AriaRole.Navigation, new() { Name = "Main navigation menu" });
        (await nav.CountAsync()).Should().BeGreaterThan(0);
        (await nav.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Dashboard: summary cards are present")]
    public async Task Dashboard_SummaryCardsArePresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();
        // MudText Typo.overline labels from Home.razor summary cards
        (await Page!.GetByText("Total Balance").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Net Worth").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Forecast Low").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Overdraft Risk").CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "Dashboard: Balance Projection section is present")]
    public async Task Dashboard_BalanceProjectionSectionPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();
        // Card heading from Home.razor
        (await Page!.GetByText("Balance Projection").CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "Dashboard: SVG chart or no-data message is present")]
    public async Task Dashboard_SvgChartOrNoDataMessagePresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();
        // role="img" aria-label="Balance projection chart" only renders when _forecastSeries.Count > 0
        // Falls back to "No forecast data" message when empty
        var chart = Page!.GetByRole(AriaRole.Img, new() { Name = "Balance projection chart" });
        var noData = Page!.GetByText("No forecast data");
        var chartCount = await chart.CountAsync();
        var noDataCount = await noData.CountAsync();
        (chartCount + noDataCount).Should().BeGreaterThan(0,
            "expected either the SVG chart or the 'no forecast data' fallback message");
    }

    [Fact(DisplayName = "Dashboard: Upcoming Flows section is present")]
    public async Task Dashboard_UpcomingFlowsSectionPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();
        (await Page!.GetByText("Upcoming Flows").CountAsync()).Should().BeGreaterThan(0);
    }
}
