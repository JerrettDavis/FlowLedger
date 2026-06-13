namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E tests for the Accounts page.
/// Verifies account management functionality including navigation, dialog, and form submission.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class AccountsTests : E2ETestBase
{
    [Fact(DisplayName = "Accounts page loads and displays title")]
    public async Task AccountsPage_LoadsAndDisplaysTitle()
    {
        // Arrange & Act
        await NavigateAsync("/accounts");

        // Assert
        var title = await Page!.TitleAsync();
        title.Should().Contain("Accounts");
    }

    [Fact(DisplayName = "Accounts page displays heading")]
    public async Task AccountsPage_DisplaysHeading()
    {
        // Arrange & Act
        await NavigateAsync("/accounts");

        // Assert
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Accounts" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await heading.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Add Account button is present")]
    public async Task AccountsPage_AddAccountButtonPresent()
    {
        // Arrange & Act
        await NavigateAsync("/accounts");

        // Assert
        var addButton = Page!.GetByRole(AriaRole.Button, new() { Name = "Add new account" });
        (await addButton.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await addButton.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Accounts grid is present")]
    public async Task AccountsPage_AccountsGridPresent()
    {
        // Arrange & Act
        await NavigateAsync("/accounts");
        await WaitForLoadAsync();

        // Assert - Check for table role (MudDataGrid renders as a table)
        var table = Page!.GetByRole(AriaRole.Table, new() { Name = "Accounts table" });
        (await table.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await table.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Navigation menu includes Accounts link")]
    public async Task AccountsPage_NavigationLinkPresent()
    {
        // Arrange & Act
        await NavigateAsync("/accounts");

        // Assert - Check that we can navigate via the menu link
        var accountsLink = Page!.GetByRole(AriaRole.Link, new() { Name = "Accounts" });
        (await accountsLink.CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "Create Account dialog button is clickable")]
    public async Task AccountsPage_CreateDialogClickable()
    {
        // Arrange
        await NavigateAsync("/accounts");

        // Act
        var addButton = Page!.GetByRole(AriaRole.Button, new() { Name = "Add new account" });
        await addButton.ClickAsync();

        // Give the dialog time to appear
        await Page!.WaitForTimeoutAsync(500);

        // Assert - The dialog or form should now be visible
        // Check for presence of form title or close button in dialog
        var dialogTitle = Page!.GetByText("Create Account");
        (await dialogTitle.CountAsync()).Should().BeGreaterThan(0);
    }
}
