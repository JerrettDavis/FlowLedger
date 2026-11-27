namespace FlowLedger.E2E.Tests;

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// #218: when the Blazor circuit drops, the reconnect UI must announce it. The status region inside the reconnect dialog is a polite live
/// region, so a screen reader hears "Rejoining the server..." and later "Rejoin failed...", and the dialog closes again once the
/// connection is back. bUnit proves the markup; this proves the browser really changes the region when the circuit drops.
///
/// The circuit's WebSocket is held through a Playwright WebSocket route: dropping it is closing that route's socket, and while the
/// outage flag is set every reconnect attempt is closed at once, so the "reconnecting" states last long enough to be observed.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class ReconnectAnnouncementTests : E2ETestBase
{
    private const string ModalSelector = "#components-reconnect-modal";
    private static readonly Regex RejoinText = new("Rejoining the server|Rejoin failed", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [E2EFact(DisplayName = "Reconnect: dropping the circuit updates the polite status region, and it clears when the connection returns")]
    public async Task Dropping_the_circuit_updates_the_polite_reconnect_status_region()
    {
        var outage = false;
        IWebSocketRoute? circuit = null;
        await Page!.RouteWebSocketAsync(new Regex(@"/_blazor"), async ws =>
        {
            if (outage)
            {
                await ws.CloseAsync();
                return;
            }

            ws.ConnectToServer();
            circuit = ws;
        });

        await NavigateAsync("/");
        await WaitForLoadAsync();
        await Page.WaitForFunctionAsync("() => typeof window.Blazor !== 'undefined'");

        var dialog = Page.Locator(ModalSelector);
        // A CSS locator: role queries skip elements inside a closed dialog, but the live region must exist before anything is announced.
        var status = dialog.Locator(".components-reconnect-container");

        // The region is a polite, atomic live region from the start (it is in the DOM even while the dialog is closed).
        await Assertions.Expect(status).ToHaveAttributeAsync("role", "status");
        await Assertions.Expect(status).ToHaveAttributeAsync("aria-live", "polite");
        await Assertions.Expect(status).ToHaveAttributeAsync("aria-atomic", "true");
        (await dialog.EvaluateAsync<bool>("d => d.open")).Should().BeFalse("nothing is announced while the circuit is healthy");

        // Wait until the page has an interactive circuit to drop.
        for (var i = 0; i < 40 && circuit is null; i++)
        {
            await Page.WaitForTimeoutAsync(250);
        }

        circuit.Should().NotBeNull("the page must have opened its circuit WebSocket");

        // Drop the circuit and keep the server unreachable.
        outage = true;
        await circuit!.CloseAsync();

        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(status.Locator("p:visible").First).ToContainTextAsync(RejoinText, new() { Timeout = 30_000 });

        // Bring the server back: the next attempt connects and the dialog closes.
        outage = false;
        await Assertions.Expect(dialog).Not.ToBeVisibleAsync(new() { Timeout = 60_000 });
    }
}
