namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>E2E smoke tests for the Subscriptions page (#70) and the rollover option on Budgets.</summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class SubscriptionsTests : E2ETestBase
{
    [E2EFact(DisplayName = "Subscriptions: page title and heading")]
    public async Task Subscriptions_PageTitleAndHeading()
    {
        await NavigateAsync("/subscriptions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        (await Page!.TitleAsync()).Should().Contain("Subscriptions");
        var heading = Page.GetByRole(AriaRole.Heading, new() { Name = "Subscriptions" });
        await Assertions.Expect(heading.First).ToBeVisibleAsync();
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Subscriptions: shows the detected table or the empty state")]
    public async Task Subscriptions_ShowsTableOrEmptyState()
    {
        await NavigateAsync("/subscriptions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var table = Page!.GetByTestId("subscriptions-table");
        var empty = Page.GetByText("No subscriptions found");
        await Assertions.Expect(table.Or(empty).First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Subscriptions: nav link is present")]
    public async Task Subscriptions_NavLinkIsPresent()
    {
        await NavigateAsync("/subscriptions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        (await Page!.GetByRole(AriaRole.Link, new() { Name = "Subscriptions" }).CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Budgets: the rollover option is offered on the budget form")]
    public async Task Budgets_RolloverOptionIsPresent()
    {
        await NavigateAsync("/budgets");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        await Assertions.Expect(Page!.GetByTestId("budget-rollover").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        AssertNoPageErrors();
    }
}
