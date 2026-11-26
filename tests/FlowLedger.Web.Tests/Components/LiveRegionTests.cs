using FluentAssertions;

namespace FlowLedger.Web.Tests.Components;

public sealed class LiveRegionTests
{
    [Fact]
    public void Reconnect_modal_status_text_is_announced_politely()
    {
        var markup = RepoFiles.Read("Components/Layout/ReconnectModal.razor");

        markup.Should().MatchRegex("""components-reconnect-container"[^>]*role="status"[^>]*aria-live="polite""");
    }
}
