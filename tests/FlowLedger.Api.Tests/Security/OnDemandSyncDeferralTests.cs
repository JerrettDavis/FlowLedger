using FlowLedger.Api.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#120: a throttled webhook sync is deferred once (coalesced), never dropped.</summary>
[Collection("ApiIntegration")]
public sealed class OnDemandSyncDeferralTests(FlowLedgerApiFactory factory)
{
    [Fact]
    public async Task Throttled_syncs_coalesce_into_exactly_one_deferred_trigger_after_the_window()
    {
        var scheduler = await factory.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var before = factory.ServerNow;

        var first = await OnDemandSyncJob.DeferAsync(scheduler, tenant, "m1", "EVT", TimeSpan.FromMinutes(20));
        var second = await OnDemandSyncJob.DeferAsync(scheduler, tenant, "m1", "EVT2", TimeSpan.FromMinutes(20));

        try
        {
            first.Should().BeTrue();
            second.Should().BeFalse("later events inside the window coalesce into the pending run");
            var triggers = await scheduler.GetTriggersOfJob(new JobKey($"deferred-{tenant:N}", "FlowLedger"));
            triggers.Should().ContainSingle();
            triggers.Single().StartTimeUtc.Should().BeAfter(before + TimeSpan.FromMinutes(19));

            // A different tenant has its own deferred run.
            (await OnDemandSyncJob.DeferAsync(scheduler, other, "m2", "EVT", TimeSpan.FromMinutes(5))).Should().BeTrue();
        }
        finally
        {
            await scheduler.DeleteJob(new JobKey($"deferred-{tenant:N}", "FlowLedger"));
            await scheduler.DeleteJob(new JobKey($"deferred-{other:N}", "FlowLedger"));
        }
    }
}
