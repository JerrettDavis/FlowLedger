using FlowLedger.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Scheduling;

/// <summary>
/// Records every job run (start, duration, outcome, error): a structured log line, the in-memory
/// <see cref="JobRunHistory"/> and the <c>flowledger.job.*</c> metrics. A job that logs an error but returns normally is
/// recorded as a success; jobs that must show up as failed throw.
/// </summary>
public sealed class JobRunRecorder(JobRunHistory history, ILogger<JobRunRecorder> logger)
{
    public void Started(string job, DateTimeOffset scheduledUtc)
        => logger.LogInformation("Job {Job} starting (scheduled {Scheduled:o}).", job, scheduledUtc);

    public JobRunRecord Finished(string job, DateTimeOffset startedUtc, DateTimeOffset finishedUtc, Exception? failure)
    {
        var duration = finishedUtc - startedUtc;
        var success = failure is null;
        var error = failure is null ? null : (failure.InnerException ?? failure).Message;

        var record = new JobRunRecord(job, startedUtc, duration, success, error);
        history.Add(record);
        FlowLedgerTelemetry.RecordJob(job, success, duration, finishedUtc);

        if (success)
        {
            logger.LogInformation("Job {Job} finished OK in {Duration:F1}s.", job, duration.TotalSeconds);
        }
        else
        {
            logger.LogError(failure, "Job {Job} FAILED after {Duration:F1}s: {Error}", job, duration.TotalSeconds, error);
        }

        return record;
    }
}

/// <summary>Quartz adapter for <see cref="JobRunRecorder"/>.</summary>
public sealed class JobRunListener(JobRunRecorder recorder, TimeProvider clock) : IJobListener
{
    public string Name => nameof(JobRunListener);

    public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        recorder.Started(context.JobDetail.Key.Name, context.ScheduledFireTimeUtc ?? context.FireTimeUtc);
        return ValueTask.CompletedTask;
    }

    public ValueTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        FlowLedgerTelemetry.RecordJobVetoed(context.JobDetail.Key.Name);
        return ValueTask.CompletedTask;
    }

    public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        var finished = clock.GetUtcNow();
        var started = context.FireTimeUtc;
        recorder.Finished(context.JobDetail.Key.Name, started, finished, jobException);
        return ValueTask.CompletedTask;
    }

    public ValueTask JobProgressChanged(IJobExecutionContext context, Quartz.Extensibility.FireInstanceProgress progress, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
