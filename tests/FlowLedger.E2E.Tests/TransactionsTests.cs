namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E tests for the Transactions page.
/// Verifies that the transactions view loads and displays data.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class TransactionsTests : E2ETestBase
{
    [Fact(DisplayName = "Transactions page loads and displays title")]
    public async Task TransactionsPage_LoadsAndDisplaysTitle()
    {
        // Arrange & Act
        await NavigateAsync("/transactions");

        // Assert
        var title = await Page!.TitleAsync();
        title.Should().Contain("Transactions");
    }

    [Fact(DisplayName = "Transactions page displays heading")]
    public async Task TransactionsPage_DisplaysHeading()
    {
        // Arrange & Act
        await NavigateAsync("/transactions");

        // Assert
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Transactions" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await heading.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Transactions grid is present")]
    public async Task TransactionsPage_GridIsPresent()
    {
        // Arrange & Act
        await NavigateAsync("/transactions");
        await WaitForLoadAsync();

        // Assert - Check for transactions table
        var table = Page!.GetByRole(AriaRole.Table, new() { Name = "Transactions table" });
        (await table.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await table.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Navigation menu includes Transactions link")]
    public async Task TransactionsPage_NavigationLinkPresent()
    {
        // Arrange & Act
        await NavigateAsync("/transactions");

        // Assert
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Transactions" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }
}
