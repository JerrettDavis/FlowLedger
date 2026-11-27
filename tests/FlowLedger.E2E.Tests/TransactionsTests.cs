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
    [E2EFact(DisplayName = "Transactions: page title contains 'Transactions'")]
    public async Task Transactions_PageTitleContainsTransactions()
    {
        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Transactions");
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Transactions: main heading is visible")]
    public async Task Transactions_MainHeadingIsVisible()
    {
        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Transactions" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Transactions: data grid is rendered")]
    public async Task Transactions_DataGridIsRendered()
    {
        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // MudDataGrid renders as <div aria-label="Transactions table"> (not a <table> element)
        var table = Page!.Locator("[aria-label='Transactions table']");
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Transactions: seeded transaction rows are visible")]
    public async Task Transactions_SeededRowsAreVisible()
    {
        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        // After seeding via EnsureSeededAsync, the SimulatedFinancialDataProvider populates
        // transactions.  WaitForGridDataAsync handles the Blazor InteractiveServer render
        // cycle: it re-waits for NetworkIdle, waits for the grid container, then polls for
        // [role='gridcell'] with a 30 s Docker-friendly budget that absorbs the full
        // SignalR circuit + API round-trip latency before asserting data is present.
        var rowCount = await WaitForGridDataAsync("[aria-label='Transactions table']");

        rowCount.Should().BeGreaterThan(0,
            "expected seeded transaction rows to appear in the Transactions table. " +
            "If 0 rows: the Web→API call may have failed (check for error alerts) or " +
            "the Blazor SignalR circuit did not render data within 30 s.");

        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Transactions: filter bar is present")]
    public async Task Transactions_FilterBarIsPresent()
    {
        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // aria-label="Search transactions" on the search field from Transactions.razor
        var filterBtn = Page!.GetByLabel("Search transactions");
        (await filterBtn.CountAsync()).Should().BeGreaterThan(0);
        (await filterBtn.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Transactions: nav link is present")]
    public async Task Transactions_NavLinkIsPresent()
    {
        await NavigateAsync("/transactions");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Transactions" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Transactions: Description, Account and Status columns are visible on a 1366x768 laptop (#209)")]
    public async Task Transactions_AllColumnsAreReachableAtLaptopWidth()
    {
        await Page!.SetViewportSizeAsync(1366, 768);
        await NavigateAsync("/transactions");
        (await WaitForGridDataAsync("[aria-label='Transactions table']")).Should().BeGreaterThan(0);

        foreach (var column in new[] { "Description", "Account", "Status" })
        {
            var header = Page.Locator("[aria-label='Transactions table'] th, [aria-label='Transactions table'] [role='columnheader']")
                .Filter(new() { HasTextString = column });
            (await header.First.IsVisibleAsync()).Should().BeTrue($"the {column} column must not be hidden at 1366 wide");
        }

        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Transactions: the amount header sorts and restarts from the first page (#209)")]
    public async Task Transactions_AmountHeaderSortsAndReloads()
    {
        await NavigateAsync("/transactions");
        (await WaitForGridDataAsync("[aria-label='Transactions table']")).Should().BeGreaterThan(0);

        // The API call happens server-side (Blazor Server), so the browser can only observe the result: the header's state flips
        // and the grid reloads. The ordering itself is asserted by TransactionSortTests and the bUnit page tests.
        await Page!.GetByTestId("sort-amount").ClickAsync();
        await Page.Locator("[data-testid=sort-amount][aria-label*='descending']").WaitForAsync();
        (await WaitForGridDataAsync("[aria-label='Transactions table']")).Should().BeGreaterThan(0);

        AssertNoPageErrors();
    }
}
