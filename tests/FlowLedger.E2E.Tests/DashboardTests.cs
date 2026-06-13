namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Dashboard page.
/// Verifies app load, title, nav, summary cards, SVG forecast chart, and upcoming-flows section.
///
/// Data expectation: the CI stack seeds data before E2E tests run (docker compose seeds
/// SimulatedFinancialDataProvider data via /api/connect + /api/sync in AccountsDataTests).
/// Dashboard tests therefore assert the REAL chart renders — "No forecast data" is treated
/// as a failure because it indicates the forecast API silently failed.
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
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Dashboard");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Dashboard: main heading is visible")]
    public async Task Dashboard_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Dashboard: navigation menu is present")]
    public async Task Dashboard_NavigationMenuIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // NavMenu renders with aria-label="Main navigation menu" (NavMenu.razor)
        var nav = Page!.GetByRole(AriaRole.Navigation, new() { Name = "Main navigation menu" });
        (await nav.CountAsync()).Should().BeGreaterThan(0);
        (await nav.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
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
        await AssertNoErrorAlertVisible();
        // MudText Typo.overline labels from Home.razor summary cards
        (await Page!.GetByText("Total Balance").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Net Worth").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Forecast Low").CountAsync()).Should().BeGreaterThan(0);
        (await Page!.GetByText("Overdraft Risk").CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
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
        await AssertNoErrorAlertVisible();
        // Card heading from Home.razor
        (await Page!.GetByText("Balance Projection").CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }

    /// <summary>
    /// Asserts that the SVG balance projection chart renders with real data.
    ///
    /// "No forecast data" is NOT accepted as success — that text appears when the forecast
    /// API silently fails (e.g. returns a non-JSON response or 500 error).  After the CI
    /// stack seeds data, the chart must be visible.  A "no data" fallback with seeded data
    /// means the forecast pipeline is broken.
    /// </summary>
    [Fact(DisplayName = "Dashboard: SVG chart renders (no-data fallback is a failure)")]
    public async Task Dashboard_SvgChartRendersWithData()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        // role="img" aria-label="Balance projection chart" only renders when _forecastSeries.Count > 0.
        // Wait up to 20 s for the chart — the forecast computation may run asynchronously.
        var chart = Page!.GetByRole(AriaRole.Img, new() { Name = "Balance projection chart" });

        await chart.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        (await chart.CountAsync()).Should().BeGreaterThan(0,
            "the Balance Projection SVG chart must be visible after data is seeded. " +
            "If 'No forecast data' is shown instead, the forecast API likely returned an error " +
            "or non-JSON response that was silently swallowed.");

        AssertNoPageErrors();
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
        await AssertNoErrorAlertVisible();
        (await Page!.GetByText("Upcoming Flows").CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }
}
