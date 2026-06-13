namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E tests for the Money Plan page.
/// Verifies that the Money Plan renders and displays balance projections.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class MoneyPlanTests : E2ETestBase
{
    [Fact(DisplayName = "Money Plan page loads and displays title")]
    public async Task MoneyPlanPage_LoadsAndDisplaysTitle()
    {
        // Arrange & Act
        await NavigateAsync("/money-plan");

        // Assert
        var title = await Page!.TitleAsync();
        title.Should().Contain("Money Plan");
    }

    [Fact(DisplayName = "Money Plan page displays heading")]
    public async Task MoneyPlanPage_DisplaysHeading()
    {
        // Arrange & Act
        await NavigateAsync("/money-plan");

        // Assert
        var heading = Page!.GetByRole(AriaRole.Heading, new() { Name = "Money Plan" });
        (await heading.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await heading.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Money Plan displays running balances")]
    public async Task MoneyPlanPage_DisplaysRunningBalances()
    {
        // Arrange & Act
        await NavigateAsync("/money-plan");
        await WaitForLoadAsync();

        // Assert - Check for the Money Plan table
        var table = Page!.GetByRole(AriaRole.Table, new() { Name = "Money plan table" });
        (await table.CountAsync()).Should().BeGreaterThan(0);
        var isVisible = await table.IsVisibleAsync();
        isVisible.Should().BeTrue();
    }

    [Fact(DisplayName = "Navigation menu includes Money Plan link")]
    public async Task MoneyPlanPage_NavigationLinkPresent()
    {
        // Arrange & Act
        await NavigateAsync("/money-plan");

        // Assert
        var link = Page!.GetByRole(AriaRole.Link, new() { Name = "Money Plan" });
        (await link.CountAsync()).Should().BeGreaterThan(0);
    }
}
