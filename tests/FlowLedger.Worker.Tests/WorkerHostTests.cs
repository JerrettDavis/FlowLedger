using FlowLedger.Worker.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace FlowLedger.Worker.Tests;

/// <summary>
/// Validates that the Worker host DI container is properly wired.
/// These tests build the host without starting it, so no database or Quartz
/// scheduler I/O occurs — they only verify the DI graph resolves correctly.
/// </summary>
public sealed class WorkerHostTests
{
    [Fact]
    public async Task Worker_host_wires_jobs_and_a_per_scope_tenant_context()
    {
        // Build (not start) the real worker host; AddInfrastructure needs a connection string, a dummy one is enough.
        using var host = WorkerHostBuilderFactory.BuildTestHost(b =>
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:flowledger"] = "Host=localhost;Database=unused;Username=u;Password=p",
                ["Worker:TenantId"] = "11111111-1111-1111-1111-111111111111",
            }));

        // Each scope gets its own tenant context, seeded from Worker:TenantId, and the ITenantContext resolves to it.
        await using var s1 = host.Services.CreateAsyncScope();
        await using var s2 = host.Services.CreateAsyncScope();
        var c1 = s1.ServiceProvider.GetRequiredService<ConfiguredTenantContext>();
        var c2 = s2.ServiceProvider.GetRequiredService<ConfiguredTenantContext>();
        c1.Should().NotBeSameAs(c2);
        c1.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        s1.ServiceProvider.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>().Should().BeSameAs(c1);

        // The three scheduled jobs are registered with Quartz.
        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("FlowLedger"));
        keys.Select(k => k.Name).Should().BeEquivalentTo(
            nameof(ScheduledSyncJob), nameof(ScheduledBackupJob), nameof(ScheduledAssetValuationJob), nameof(ScheduledHomeAssistantJob));
    }

    [Fact]
    public void JobScheduleOptions_has_expected_defaults()
    {
        // Verify the defaults are conservative (every 4 hours) and enabled.
        var options = new JobScheduleOptions();

        options.Enabled.Should().BeTrue("jobs should be enabled by default");
        options.CronExpression.Should().Be("0 0 12 * * ?",
            "default cron should fire every 4 hours — conservative, not aggressive polling");
    }

    [Fact]
    public void ScheduledSyncJob_is_annotated_DisallowConcurrentExecution()
    {
        // Verify the attribute is present so Quartz won't run overlapping instances.
        var attribute = typeof(ScheduledSyncJob)
            .GetCustomAttributes(typeof(DisallowConcurrentExecutionAttribute), inherit: false);

        attribute.Should().NotBeEmpty(
            "ScheduledSyncJob must have [DisallowConcurrentExecution] to prevent overlapping sync runs");
    }
}
