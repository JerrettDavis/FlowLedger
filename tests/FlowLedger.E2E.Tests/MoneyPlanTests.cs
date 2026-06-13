namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Money Plan page.
/// Verifies page load, heading, running-balance spreadsheet grid, and nav link.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class MoneyPlanTests : E2ETestBase
{
    [Fact(DisplayName = "MoneyPlan: page title contains 'Money Plan'")]
    public async Task MoneyPlan_PageTitleContainsMoneyPlan()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/money-plan");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Money Plan");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "MoneyPlan: main heading is visible")]
    public async Task MoneyPlan_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/money-plan");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Money Plan" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "MoneyPlan: spreadsheet grid is rendered")]
    public async Task MoneyPlan_SpreadsheetGridIsRendered()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/money-plan");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // MudDataGrid renders as <div aria-label="Money plan spreadsheet"> (not a <table> element)
        var table = Page!.Locator("[aria-label='Money plan spreadsheet']");
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "MoneyPlan: spreadsheet grid contains data rows")]
    public async Task MoneyPlan_SpreadsheetGridContainsDataRows()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/money-plan");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        var table = Page!.Locator("[aria-label='Money plan spreadsheet']");
        await table.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        // MudDataGrid data rows contain cells with role="gridcell"
        var rows = table.Locator("[role='gridcell']");
        var rowCount = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && rowCount == 0)
        {
            rowCount = await rows.CountAsync();
            if (rowCount == 0)
            {
                await Page!.WaitForTimeoutAsync(500);
            }
        }

        rowCount.Should().BeGreaterThan(0,
            "expected seeded data rows to appear in the Money Plan spreadsheet. " +
            "If 0 rows: the Web→API call may have failed or Blazor did not load data in time.");

        AssertNoPageErrors();
    }

    [Fact(DisplayName = "MoneyPlan: nav link is present")]
    public async Task MoneyPlan_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/money-plan");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Money Plan" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }
}
