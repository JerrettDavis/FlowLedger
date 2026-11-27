using FlowLedger.Worker.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    /// <summary>Completes <paramref name="exhausted"/> when the retry scheduler reports that it gave up on a job.</summary>
    private sealed class ExhaustionSignal(TaskCompletionSource exhausted) : ILogger<JobRetryScheduler>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("will not be retried", StringComparison.Ordinal))
            {
                exhausted.TrySetResult();
            }
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
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddSingleton<ILogger<JobRetryScheduler>>(new ExhaustionSignal(exhausted));
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

            // The scheduler logs "will not be retried" exactly when it declines to schedule another attempt, so that is the completion
            // signal: no polling, and it cannot pass early (a fourth run would have to be scheduled before this line is logged).
            await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            (await scheduler.GetTriggersOfJob(new JobKey("ScheduledBackupJob", "FlowLedger"))).Should().BeEmpty("no retry trigger may be left after exhaustion");
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
