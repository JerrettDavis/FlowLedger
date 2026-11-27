using FlowLedger.Infrastructure.Observability;
using Quartz;

namespace FlowLedger.Worker.Scheduling;

/// <summary>
/// Bounded retry of a failed scheduled job (<c>Worker:JobRetry:*</c>). <c>MaxAttempts</c> is the total number of runs including
/// the first (default 3; 1 disables retries). Delays grow exponentially from <c>BaseDelaySeconds</c> (default 300) up to
/// <c>MaxDelaySeconds</c> (default 3600) and are jittered by +/-50% so replicas and jobs do not retry in lockstep.
/// </summary>
internal sealed record JobRetryOptions(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    public static JobRetryOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Worker:JobRetry");
        return new JobRetryOptions(
            section.GetValue("MaxAttempts", 3),
            TimeSpan.FromSeconds(section.GetValue("BaseDelaySeconds", 300)),
            TimeSpan.FromSeconds(section.GetValue("MaxDelaySeconds", 3600)));
    }

    /// <summary>
    /// Jobs worth re-running after a failure. The sync job is deliberately absent: every retry would be another provider call (cost),
    /// and it already applies its own per-connection back-off (<c>SyncRetryPolicy</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> RetryableJobs = new HashSet<string>(StringComparer.Ordinal)
    {
        "ScheduledBackupJob",
        "ScheduledAssetValuationJob",
        "ScheduledHomeAssistantJob",
    };

    /// <summary>Delay before the next attempt after <paramref name="completedAttempts"/> runs failed, or null when attempts are exhausted.</summary>
    /// <param name="random">Returns a value in [0, 1); injectable for tests.</param>
    public TimeSpan? NextDelay(int completedAttempts, Func<double> random)
    {
        if (completedAttempts < 1 || completedAttempts >= MaxAttempts)
        {
            return null;
        }

        var exponential = BaseDelay.TotalSeconds * Math.Pow(2, completedAttempts - 1);
        var capped = Math.Min(exponential, MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(capped * (0.5 + random()));
    }
}

/// <summary>Schedules a one-off retry trigger for a failed job.</summary>
internal sealed partial class JobRetryScheduler(JobRetryOptions options, TimeProvider clock, ILogger<JobRetryScheduler> logger)
{
    public const string AttemptKey = "retryAttempt";

    public static int AttemptOf(IJobExecutionContext context)
        => context.MergedJobDataMap.ContainsKey(AttemptKey) && int.TryParse(context.MergedJobDataMap[AttemptKey]?.ToString(), out var n) ? n : 1;

    public async ValueTask TryScheduleAsync(IJobExecutionContext context, CancellationToken ct)
    {
        var job = context.JobDetail.Key;
        if (!JobRetryOptions.RetryableJobs.Contains(job.Name))
        {
            return;
        }

        var attempt = AttemptOf(context);
        if (options.NextDelay(attempt, Random.Shared.NextDouble) is not { } delay)
        {
            LogNotRetried(job.Name, attempt);
            return;
        }

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{job.Name}-retry-{Guid.NewGuid():N}", job.Group)
            .ForJob(job)
            .StartAt(clock.GetUtcNow() + delay)
            .UsingJobData(AttemptKey, attempt + 1)
            .Build();
        await context.Scheduler.ScheduleJob(trigger, new ScheduleJobOptions(), ct);
        FlowLedgerTelemetry.RecordJobRetry(job.Name);
        LogRetrying(job.Name, attempt, delay.TotalSeconds, attempt + 1, options.MaxAttempts);
    }

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Job {Job} failed on attempt {Attempt} and will not be retried (attempts exhausted or retries disabled).")]
    private partial void LogNotRetried(string job, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Job {Job} failed on attempt {Attempt}; retrying in {Delay:F0}s (attempt {Next} of {Max}).")]
    private partial void LogRetrying(string job, int attempt, double delay, int next, int max);
}
