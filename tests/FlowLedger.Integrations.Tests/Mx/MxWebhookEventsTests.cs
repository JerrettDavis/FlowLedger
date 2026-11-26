using FlowLedger.Integrations.Mx;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>The MX webhook event-type allow-list (#120): only events that mean transactions or accounts changed trigger a sync.</summary>
public sealed class MxWebhookEventsTests
{
    [Theory]
    [InlineData("aggregation")]
    [InlineData("AGGREGATION")]
    [InlineData("history")]
    [InlineData("account")]
    [InlineData("transactions")]
    public void Default_allow_list_triggers_a_sync_for_data_changing_events(string type)
        => MxWebhookEvents.IsSyncTrigger(type).Should().BeTrue();

    [Theory]
    [InlineData("member")]
    [InlineData("identification")]
    [InlineData("statement")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_events_and_unparseable_types_do_not_trigger_a_sync(string? type)
        => MxWebhookEvents.IsSyncTrigger(type).Should().BeFalse();

    [Fact]
    public void Configured_list_replaces_the_default()
    {
        string[] configured = ["statement"];
        MxWebhookEvents.IsSyncTrigger("statement", configured).Should().BeTrue();
        MxWebhookEvents.IsSyncTrigger("aggregation", configured).Should().BeFalse();
        MxWebhookEvents.IsSyncTrigger("aggregation", []).Should().BeFalse("an empty list syncs on nothing");
    }
}
