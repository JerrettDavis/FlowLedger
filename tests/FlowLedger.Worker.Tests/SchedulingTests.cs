using System.Diagnostics.Metrics;
using FlowLedger.Infrastructure.Observability;
using FlowLedger.TestSupport;
using FlowLedger.Worker.Jobs;
using FlowLedger.Worker.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace FlowLedger.Worker.Tests;

public sealed class SchedulingTests
{
    private static readonly Dictionary<string, string?> BaseConfig = new()
    {
        ["ConnectionStrings:flowledger"] = "Host=localhost;Database=unused;Username=u;Password=p",
    };

    [Fact]
    public async Task Every_cron_trigger_fires_once_now_after_a_misfire()
    {
        using var host = WorkerHostBuilderFactory.BuildTestHost(b => b.Configuration.AddInMemoryCollection(BaseConfig));
        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

        var triggers = new List<ITrigger>();
        foreach (var key in await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.GroupEquals("FlowLedger")))
        {
            triggers.Add((await scheduler.GetTrigger(key))!);
        }

        triggers.Should().HaveCount(4);
        triggers.Should().OnlyContain(t => t is ICronTrigger && ((ICronTrigger)t).MisfireInstruction == CronTriggerMisfireInstruction.FireAndProceed);
    }

    [Fact]
    public async Task Run_recording_and_cross_process_exclusion_listeners_are_attached_to_the_scheduler()
    {
        using var host = WorkerHostBuilderFactory.BuildTestHost(b => b.Configuration.AddInMemoryCollection(BaseConfig));
        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

        scheduler.ListenerManager.GetJobListeners().Select(l => l.Name).Should().Contain(nameof(JobRunListener));
        scheduler.ListenerManager.GetTriggerListeners().Select(l => l.Name).Should().Contain(nameof(JobExclusionTriggerListener));
    }

    [Fact]
    public void Every_scheduled_job_disallows_concurrent_execution()
    {
        var jobs = typeof(ScheduledSyncJob).Assembly.GetTypes().Where(t => typeof(IJob).IsAssignableFrom(t) && !t.IsAbstract).ToList();

        jobs.Should().NotBeEmpty();
        jobs.Should().OnlyContain(t => t.GetCustomAttributes(typeof(DisallowConcurrentExecutionAttribute), false).Length > 0);
    }

    [Fact]
    public async Task Failing_job_is_recorded_in_history_and_metrics_and_success_updates_last_success()
    {
        var failures = new List<long>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == FlowLedgerTelemetry.MeterName && inst.Name == "flowledger.job.runs")
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "outcome" && (string?)t.Value == "failure") && tags.ToArray().Any(t => (string?)t.Value == "sched-test-job"))
            {
                failures.Add(value);
            }
        });
        meterListener.Start();

        var history = new JobRunHistory();
        var recorder = new JobRunRecorder(history, NullLogger<JobRunRecorder>.Instance);
        var t0 = TestClock.Instant;

        recorder.Finished("sched-test-job", t0, t0.AddSeconds(2), new JobExecutionException("boom"));
        recorder.Finished("sched-test-job", t0, t0.AddSeconds(1), null);

        var runs = history.Recent();
        runs.Should().HaveCount(2);
        runs[1].Success.Should().BeFalse();
        runs[1].Error.Should().Be("boom");
        runs[0].Success.Should().BeTrue();
        failures.Should().ContainSingle();
    }
}
