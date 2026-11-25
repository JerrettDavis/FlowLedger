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

    [Fact(DisplayName = "RecurringFlows: flow list is rendered")]
    public async Task RecurringFlows_DataGridIsRendered()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var flows = Page!.Locator("section[aria-label='Bills & expenses'] .fl-li, section[aria-label='Income'] .fl-li");
        await Assertions.Expect(flows.First).ToBeVisibleAsync();
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

        var flows = Page!.Locator("section[aria-label='Bills & expenses'] .fl-li, section[aria-label='Income'] .fl-li");
        await Assertions.Expect(flows.First).ToBeVisibleAsync(new() { Timeout = 30_000 });

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
