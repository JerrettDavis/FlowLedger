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
        var title = await Page!.TitleAsync();
        title.Should().Contain("Accounts");
    }

    [Fact(DisplayName = "Accounts: main heading is visible")]
    public async Task Accounts_MainHeadingIsVisible()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Accounts" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        (await heading.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Accounts: 'Add Account' button is present")]
    public async Task Accounts_AddAccountButtonIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        // aria-label="Add new account" from Accounts.razor
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Add new account" });
        (await btn.CountAsync()).Should().BeGreaterThan(0);
        (await btn.IsVisibleAsync()).Should().BeTrue();
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
        // MudDataGrid aria-label="Accounts table" from Accounts.razor
        var table = Page!.GetByRole(AriaRole.Table, new() { Name = "Accounts table" });
        (await table.CountAsync()).Should().BeGreaterThan(0);
        (await table.IsVisibleAsync()).Should().BeTrue();
    }

    [Fact(DisplayName = "Accounts: nav link is present")]
    public async Task Accounts_NavLinkIsPresent()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Accounts" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "Accounts: clicking Add Account opens Create Account dialog")]
    public async Task Accounts_ClickingAddAccountOpensDialog()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/accounts");
        var btn = Page!.GetByRole(AriaRole.Button, new() { Name = "Add new account" });
        await btn.ClickAsync();
        await Page!.WaitForTimeoutAsync(500);
        // MudDialog title "Create Account" from DialogService.ShowAsync in Accounts.razor
        (await Page!.GetByText("Create Account").CountAsync()).Should().BeGreaterThan(0);
    }
}
