namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Transactions page.
/// Verifies page load, heading, data grid, filter bar, and nav link.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class TransactionsTests : E2ETestBase
{
    [Fact(DisplayName = "Transactions: page title contains 'Transactions'")]
    public async Task Transactions_PageTitleContainsTransactions()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        var title = await Page!.TitleAsync();
        title.Should().Contain("Transactions");
    }

    [Fact(DisplayName = "Transactions: main heading is visible")]
    public async Task Transactions_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Transactions" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Transactions: data grid is rendered")]
    public async Task Transactions_DataGridIsRendered()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        // MudDataGrid renders as <div aria-label="Transactions table"> (not a <table> element)
        var table = Page!.Locator("[aria-label='Transactions table']");
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Transactions: filter bar is present")]
    public async Task Transactions_FilterBarIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        // aria-label="Apply filters" on the Filter button from Transactions.razor
        var filterBtn = Page!.GetByRole(AriaRole.Button, new() { Name = "Apply filters" });
        (await filterBtn.CountAsync()).Should().BeGreaterThan(0);
        (await filterBtn.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Transactions: nav link is present")]
    public async Task Transactions_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Transactions" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }
}
