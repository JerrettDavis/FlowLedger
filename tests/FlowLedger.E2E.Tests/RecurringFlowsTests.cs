namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E tests for the Recurring Flows page.
/// Verifies that the page loads and provides UI for managing recurring flows.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class RecurringFlowsTests : E2ETestBase
{
    [Fact(DisplayName = "Recurring Flows page loads and displays title")]
    public async Task RecurringFlowsPage_LoadsAndDisplaysTitle()
    {
        // Arrange & Act
        await NavigateAsync("/recurring-flows");

        // Assert
        var title = await Page!.TitleAsync();
        title.Should().Contain("Recurring Flows");
    }

    [Fact(DisplayName = "Recurring Flows page displays heading")]
    public async Task RecurringFlowsPage_DisplaysHeading()
    {
        // Arrange & Act
        await NavigateAsync("/recurring-flows");

        // Assert
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Recurring Flows" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await heading.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Add Recurring Flow button is present")]
    public async Task RecurringFlowsPage_AddButtonPresent()
    {
        // Arrange & Act
        await NavigateAsync("/recurring-flows");

        // Assert
        var addButton = Page!.GetByRole(AriaRole.Button, new() { Name = "Add new recurring flow" });
        (await addButton.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await addButton.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Recurring Flows grid is present")]
    public async Task RecurringFlowsPage_GridPresent()
    {
        // Arrange & Act
        await NavigateAsync("/recurring-flows");
        await WaitForLoadAsync();

        // Assert - Check for table role
        var table = Page!.GetByRole(AriaRole.Table, new() { Name = "Recurring flows table" });
        (await table.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await table.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Navigation menu includes Recurring Flows link")]
    public async Task RecurringFlowsPage_NavigationLinkPresent()
    {
        // Arrange & Act
        await NavigateAsync("/recurring-flows");

        // Assert
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Recurring Flows" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }
}
