using FlowLedger.Worker.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace FlowLedger.Worker.Tests;

public sealed class JobRetryTests
{
    private static readonly JobRetryOptions Options = new(MaxAttempts: 4, BaseDelay: TimeSpan.FromSeconds(10), MaxDelay: TimeSpan.FromSeconds(60));

    [Fact]
    public void Delay_grows_exponentially_is_capped_and_is_exhausted_after_max_attempts()
    {
        Options.NextDelay(1, () => 0.5)!.Value.Should().Be(TimeSpan.FromSeconds(10));
        Options.NextDelay(2, () => 0.5)!.Value.Should().Be(TimeSpan.FromSeconds(20));
        Options.NextDelay(3, () => 0.5)!.Value.Should().Be(TimeSpan.FromSeconds(40));
        Options.NextDelay(4, () => 0.5).Should().BeNull("the fourth run was the last allowed attempt");
        new JobRetryOptions(10, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)).NextDelay(5, () => 0.5)!.Value.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Jitter_spreads_the_delay_between_half_and_one_and_a_half_times()
    {
        Options.NextDelay(1, () => 0.0)!.Value.Should().Be(TimeSpan.FromSeconds(5));
        Options.NextDelay(1, () => 0.999)!.Value.Should().BeCloseTo(TimeSpan.FromSeconds(14.99), TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void One_attempt_disables_retries()
        => new JobRetryOptions(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).NextDelay(1, () => 0.5).Should().BeNull();

    public sealed class FailingJob : IJob
    {
        public static int Runs;
        public static readonly List<int> Attempts = [];

        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            lock (Attempts)
            {
                Attempts.Add(JobRetryScheduler.AttemptOf(context));
            }

            throw new JobExecutionException("boom");
        }
    }

    [Fact]
    public async Task A_failing_retryable_job_is_rerun_with_an_increasing_attempt_until_attempts_are_exhausted()
    {
        FailingJob.Runs = 0;
        FailingJob.Attempts.Clear();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new JobRetryOptions(3, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)));
        builder.Services.AddSingleton<JobRetryScheduler>();
        builder.Services.AddSingleton<JobRunHistory>();
        builder.Services.AddSingleton(sp => new JobRunRecorder(sp.GetRequiredService<JobRunHistory>(), NullLogger<JobRunRecorder>.Instance));
        builder.Services.AddSingleton<JobRunListener>();
        builder.Services.AddSingleton(NullLogger<JobRetryScheduler>.Instance);
        builder.Services.AddQuartz(q =>
        {
            q.AddJob<FailingJob>(o => o.WithIdentity("ScheduledBackupJob", "FlowLedger").StoreDurably());
            q.AddJobListener<JobRunListener>();
        });
        builder.Services.AddQuartzHostedService();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
            await scheduler.TriggerJob(new JobKey("ScheduledBackupJob", "FlowLedger"));

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Volatile.Read(ref FailingJob.Runs) < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await Task.Delay(500); // a fourth run would have been scheduled by now if exhaustion were not honoured
            FailingJob.Runs.Should().Be(3);
            FailingJob.Attempts.Should().Equal(1, 2, 3);
            host.Services.GetRequiredService<JobRunHistory>().Recent().Should().HaveCount(3).And.OnlyContain(r => !r.Success);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
