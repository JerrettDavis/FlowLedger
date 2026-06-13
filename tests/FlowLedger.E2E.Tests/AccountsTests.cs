namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E smoke tests for the Accounts page.
/// Verifies navigation, page heading, grid, and create-account dialog.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class AccountsTests : E2ETestBase
{
    [Fact(DisplayName = "Accounts: page title contains 'Accounts'")]
    public async Task Accounts_PageTitleContainsAccounts()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var title = await Page!.TitleAsync();
        title.Should().Contain("Accounts");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Accounts: main heading is visible")]
    public async Task Accounts_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Accounts" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Accounts: 'Add Account' button is present")]
    public async Task Accounts_AddAccountButtonIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // aria-label="Add new account" from Accounts.razor
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Add new account" });
        (await btn.CountAsync()).Should().BeGreaterThan(0);
        (await btn.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Accounts: data grid is rendered")]
    public async Task Accounts_DataGridIsRendered()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // MudDataGrid renders as <div aria-label="Accounts table"> (not a <table> element)
        var table = Page!.Locator("[aria-label='Accounts table']");
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Accounts: nav link is present")]
    public async Task Accounts_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Accounts" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Accounts: clicking Add Account opens Create Account dialog")]
    public async Task Accounts_ClickingAddAccountOpensDialog()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        // Blazor Server establishes its SignalR circuit via WebSocket AFTER Playwright's
        // NetworkIdle fires (Playwright does not track WebSocket traffic as network activity).
        // Clicking before the circuit is live is silently ignored by the browser.
        // Use the retry helper to keep clicking until the dialog appears.
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Add new account" });
        await ClickUntilVisibleAsync(btn, "Create Account", intervalMs: 600, timeoutMs: 15000);
        // MudDialog title "Create Account" from DialogService.ShowAsync in Accounts.razor
        var dialogTitle = Page!.GetByText("Create Account");
        await dialogTitle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        (await dialogTitle.CountAsync()).Should().BeGreaterThan(0);
        AssertNoPageErrors();
    }
}
