namespace FlowLedger.E2E.Tests;

using FluentAssertions;
using Microsoft.Playwright;

/// <summary>E2E smoke tests for the Data &amp; Backups page, including the in-app restore-drill summary (#62).</summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class DataBackupsTests : E2ETestBase
{
    [E2EFact(DisplayName = "Data: page title and heading")]
    public async Task Data_PageTitleAndHeading()
    {
        await NavigateAsync("/data");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        (await Page!.TitleAsync()).Should().Contain("Data");
        await Assertions.Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Data & Backups" }).First).ToBeVisibleAsync();
        AssertNoPageErrors();
    }

    [E2EFact(DisplayName = "Data: the restore drill card shows the last drill or says none has run")]
    public async Task Data_RestoreDrillCardIsPresent()
    {
        await NavigateAsync("/data");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        var card = Page!.GetByTestId("restore-drill");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var result = card.GetByTestId("restore-drill-result");
        var none = card.GetByTestId("restore-drill-none");
        await Assertions.Expect(result.Or(none).First).ToBeVisibleAsync();
        AssertNoPageErrors();
    }
}
