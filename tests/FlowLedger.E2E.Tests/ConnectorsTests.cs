namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>The Connectors page lists every financial data feed, marks the one in use, and never shows credentials.</summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class ConnectorsTests : E2ETestBase
{
    [Fact(DisplayName = "Connectors: lists all feeds and marks the active one")]
    public async Task Connectors_ListsAllFeedsAndMarksTheActiveOne()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/connectors");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        await Assertions.Expect(Page!.GetByText("Simulated demo data", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(Page.GetByText("SimpleFIN Bridge", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(Page.GetByText("MX", new() { Exact = true }).First).ToBeVisibleAsync();
        await Assertions.Expect(Page.GetByText("In use", new() { Exact = true })).ToHaveCountAsync(1);

        await Page.ScreenshotAsync(new() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "connectors-page.png"), FullPage = true });
        var text = await Page.Locator("body").InnerTextAsync();
        text.Should().Contain("Active connector: Simulated").And.NotContain("@");
        AssertNoPageErrors();
    }
}
