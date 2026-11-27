using System.Text;
using FlowLedger.Api.Endpoints;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Trait("Invariant", "INV-10")]
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

    [Fact]
    public void In_process_map_stays_bounded_after_ten_times_the_cap_of_distinct_live_bodies()
    {
        // Timestamps sit far in the past so any entries other tests hold (newer) are never the oldest to be evicted.
        var now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < WebhookReplayGuard.MaxEntries * 10; i++)
        {
            WebhookReplayGuard.SeenRecently(Encoding.UTF8.GetBytes("bound-" + Guid.NewGuid()), now.AddMilliseconds(i));
        }

        WebhookReplayGuard.TrackedCount.Should().BeLessThanOrEqualTo(WebhookReplayGuard.MaxEntries);
    }
}
