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
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Transactions");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Transactions: main heading is visible")]
    public async Task Transactions_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Transactions" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
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
        await AssertNoErrorAlertVisible();
        // MudDataGrid renders as <div aria-label="Transactions table"> (not a <table> element)
        var table = Page!.Locator("[aria-label='Transactions table']");
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Transactions: seeded transaction rows are visible")]
    public async Task Transactions_SeededRowsAreVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        // After seeding via AccountsDataTests.SeedDataAsync (or CI seed step), the
        // SimulatedFinancialDataProvider populates transactions.  Wait for at least one
        // data row to appear — the grid has role="row" cells inside aria-label="Transactions table".
        // We probe for any MudDataGrid row content that is not the header row.
        var table = Page!.Locator("[aria-label='Transactions table']");
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
            "expected seeded transaction rows to appear in the Transactions table. " +
            "If 0 rows: the Web→API call may have failed (check for error alerts) or " +
            "the Blazor SignalR circuit did not load data in time.");

        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Transactions: filter bar is present")]
    public async Task Transactions_FilterBarIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // aria-label="Apply filters" on the Filter button from Transactions.razor
        var filterBtn = Page!.GetByRole(AriaRole.Button, new() { Name = "Apply filters" });
        (await filterBtn.CountAsync()).Should().BeGreaterThan(0);
        (await filterBtn.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Transactions: nav link is present")]
    public async Task Transactions_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Transactions" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }
}
