using System.Text;
using FlowLedger.Integrations.Mx;
using Microsoft.Extensions.Configuration;

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
        MxWebhookEvents.IsSyncTrigger("aggregation", []).Should().BeFalse();
    }

    private static IConfiguration Config(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    [Fact]
    public void Nothing_configured_uses_the_default()
        => MxWebhookEvents.FromConfiguration(Config("{}")).Should().BeEquivalentTo(MxWebhookEvents.DefaultSyncEventTypes);

    [Theory]
    [InlineData("""{"Mx":{"WebhookSyncEventTypes":"statement, History"}}""")]
    [InlineData("""{"Mx":{"WebhookSyncEventTypes":["statement","History"]}}""")]
    public void A_comma_separated_string_or_a_json_array_replaces_the_default(string json)
    {
        var list = MxWebhookEvents.FromConfiguration(Config(json));

        list.Should().BeEquivalentTo(["statement", "History"]);
        MxWebhookEvents.IsSyncTrigger("HISTORY", list).Should().BeTrue();
        MxWebhookEvents.IsSyncTrigger("aggregation", list).Should().BeFalse();
    }

    [Fact]
    public void An_empty_array_falls_back_to_the_default_because_configuration_drops_it()
        => MxWebhookEvents.FromConfiguration(Config("""{"Mx":{"WebhookSyncEventTypes":[]}}""")).Should().BeEquivalentTo(MxWebhookEvents.DefaultSyncEventTypes);

    [Fact]
    public void The_single_value_none_disables_webhook_triggered_syncs()
    {
        var list = MxWebhookEvents.FromConfiguration(Config("""{"Mx":{"WebhookSyncEventTypes":"none"}}"""));

        list.Should().BeEmpty();
        MxWebhookEvents.IsSyncTrigger("aggregation", list).Should().BeFalse();
    }
}
