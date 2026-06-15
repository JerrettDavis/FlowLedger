namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Recurring Flows page.
/// Verifies page load, heading, grid, add-flow button, and data presence.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class RecurringFlowsTests : E2ETestBase
{
    [Fact(DisplayName = "RecurringFlows: page title contains 'Recurring Flows'")]
    public async Task RecurringFlows_PageTitleContainsRecurringFlows()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Recurring Flows");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "RecurringFlows: main heading is visible")]
    public async Task RecurringFlows_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Recurring Flows" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "RecurringFlows: 'Add Flow' button is present")]
    public async Task RecurringFlows_AddFlowButtonIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // aria-label="Add recurring flow" from RecurringFlows.razor
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Add recurring flow" });
        (await btn.CountAsync()).Should().BeGreaterThan(0);
        (await btn.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "RecurringFlows: data grid is rendered")]
    public async Task RecurringFlows_DataGridIsRendered()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // MudDataGrid renders as <div aria-label="Recurring flows table"> (not a <table> element)
        var table = Page!.Locator("[aria-label='Recurring flows table']");
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "RecurringFlows: seeded flow rows are visible")]
    public async Task RecurringFlows_SeededRowsAreVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        var table = Page!.Locator("[aria-label='Recurring flows table']");
        await table.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });

        // MudBlazor 9 renders MudDataGrid rows as standard HTML table elements (tbody/tr/td).
        // Poll tbody td elements — header th cells are in thead and are not counted here.
        var rows = table.Locator("tbody td");
        var rowCount = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && rowCount == 0)
        {
            rowCount = await rows.CountAsync();
            if (rowCount == 0)
            {
                await Page!.WaitForTimeoutAsync(500);
            }
        }

        rowCount.Should().BeGreaterThan(0,
            "expected seeded recurring flow rows to appear in the Recurring Flows table. " +
            "If 0 rows: the Web→API call may have failed or Blazor did not load data in time.");

        AssertNoPageErrors();
    }

    [Fact(DisplayName = "RecurringFlows: nav link is present")]
    public async Task RecurringFlows_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Recurring Flows" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }
}
