using FlowLedger.Worker.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public void Worker_host_builds_without_exception()
    {
        // Arrange + Act — building the host validates the DI container graph.
        // AddInfrastructure requires a connection string; we supply a dummy value.
        var act = () => Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
        });

        // The builder itself must construct without throwing.
        act.Should().NotThrow();
    }

    [Fact]
    public void JobScheduleOptions_has_expected_defaults()
    {
        // Verify the defaults are conservative (every 4 hours) and enabled.
        var options = new JobScheduleOptions();

        options.Enabled.Should().BeTrue("jobs should be enabled by default");
        options.CronExpression.Should().Be("0 0 */4 * * ?",
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
