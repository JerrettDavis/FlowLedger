using System.Text;
using FlowLedger.Api.Endpoints;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

public sealed class WebhookReplayGuardTests
{
    [Fact]
    public void Same_body_inside_window_is_a_replay_but_a_different_body_is_not()
    {
        var t0 = DateTimeOffset.UtcNow;
        var body = Encoding.UTF8.GetBytes("{\"id\":\"" + Guid.NewGuid() + "\"}");

        WebhookReplayGuard.SeenRecently(body, t0).Should().BeFalse();
        WebhookReplayGuard.SeenRecently(body, t0.AddMinutes(1)).Should().BeTrue();
        WebhookReplayGuard.SeenRecently(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()), t0).Should().BeFalse();
    }

    [Fact]
    public void Same_body_after_window_is_processed_again()
    {
        var t0 = DateTimeOffset.UtcNow;
        var body = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());

        WebhookReplayGuard.SeenRecently(body, t0).Should().BeFalse();
        WebhookReplayGuard.SeenRecently(body, t0.AddMinutes(11)).Should().BeFalse();
    }
}
