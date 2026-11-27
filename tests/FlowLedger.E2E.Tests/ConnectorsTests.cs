namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>The Connectors page lists every financial data feed, marks the one in use, and never shows credentials.</summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class ConnectorsTests : E2ETestBase
{
    private static HttpClient Api() => E2EApi.CreateClient();

    [E2EFact(DisplayName = "Connectors: lists all feeds and marks the enabled one")]
    public async Task Connectors_ListsAllFeedsAndMarksTheActiveOne()
    {
        await NavigateAsync("/connectors");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        await Assertions.Expect(Page!.GetByText("Simulated demo data", new() { Exact = true }).First).ToBeVisibleAsync();
        await Assertions.Expect(Page.GetByText("SimpleFIN Bridge", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(Page.GetByText("MX", new() { Exact = true }).First).ToBeVisibleAsync();
        await Assertions.Expect(Page.GetByText("Enabled", new() { Exact = true })).ToHaveCountAsync(1);

        await Page.ScreenshotAsync(new() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "connectors-page.png"), FullPage = true });
        var text = await Page.Locator("body").InnerTextAsync();
        text.Should().Contain("Enabled on this instance: Simulated demo data").And.NotContain("@");
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Accounts: shows each account's data source and lets the owner open the data-source dialog")]
    public async Task Accounts_ShowDataSourceAndOpenDialog()
    {
        // Make sure at least one connector-fed account exists.
        using (var http = Api())
        {
            await http.PostAsync("api/connect", null);
            await http.PostAsync("api/sync", null);
        }

        await Page!.SetViewportSizeAsync(1800, 900);
        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();

        var chips = Page!.Locator(".fl-data-source");
        await Assertions.Expect(chips.First).ToBeVisibleAsync();
        (await chips.First.InnerTextAsync()).Should().Contain("Simulated");

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("Change data source for") }).First.ClickAsync();
        await Assertions.Expect(Page.GetByText("Data source for", new() { Exact = false }).First).ToBeVisibleAsync();
        await Assertions.Expect(Page.GetByText("Choose which connector feeds this account", new() { Exact = false })).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        AssertNoPageErrors();
    }
}
