namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Recurring Flows page.
/// Verifies page load, heading, grid, and add-flow button.
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
        var title = await Page!.TitleAsync();
        title.Should().Contain("Recurring Flows");
    }

    [Fact(DisplayName = "RecurringFlows: main heading is visible")]
    public async Task RecurringFlows_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Recurring Flows" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "RecurringFlows: 'Add Flow' button is present")]
    public async Task RecurringFlows_AddFlowButtonIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        // aria-label="Add recurring flow" from RecurringFlows.razor
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Add recurring flow" });
        (await btn.CountAsync()).Should().BeGreaterThan(0);
        (await btn.IsVisibleAsync()).Should().BeTrue();
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
        // MudDataGrid renders as <div aria-label="Recurring flows table"> (not a <table> element)
        var table = Page!.Locator("[aria-label='Recurring flows table']");
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "RecurringFlows: nav link is present")]
    public async Task RecurringFlows_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Recurring Flows" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }
}
