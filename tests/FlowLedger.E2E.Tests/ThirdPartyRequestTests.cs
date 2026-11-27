namespace FlowLedger.E2E.Tests;

using FluentAssertions;

/// <summary>
/// A self-hosted finance app must not phone home: every request a page makes goes to the app's own origin (#214).
/// Fonts are self-hosted (wwwroot/fonts), the CSP lists no external host, and this watches the real network.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class ThirdPartyRequestTests : E2ETestBase
{
    [E2ETheory(DisplayName = "Privacy: pages make no third-party requests")]
    [InlineData("/")]
    [InlineData("/transactions")]
    [InlineData("/accounts")]
    public async Task Page_requests_stay_on_the_app_origin(string route)
    {
        var appOrigin = new Uri(BaseUrl).GetLeftPart(UriPartial.Authority);
        var foreign = new System.Collections.Concurrent.ConcurrentBag<string>();
        Page!.Request += (_, request) =>
        {
            var url = request.Url;
            // data: and blob: URIs never leave the browser; everything else must share the app's origin.
            if (!url.StartsWith("data:", StringComparison.Ordinal)
                && !url.StartsWith("blob:", StringComparison.Ordinal)
                && !url.StartsWith("about:", StringComparison.Ordinal)
                && !url.StartsWith(appOrigin, StringComparison.OrdinalIgnoreCase))
            {
                foreign.Add(url);
            }
        };

        await NavigateAsync(route);
        await WaitForLoadAsync();

        foreign.Should().BeEmpty("no font, script, image or tracking request may leave the app's own origin");
        AssertNoPageErrors();
    }
}
