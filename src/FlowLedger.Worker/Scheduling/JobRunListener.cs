using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Scheduling;

/// <summary>
/// Records every job run (start, duration, outcome, error): a structured log line, the in-memory
/// <see cref="JobRunHistory"/> and the <c>flowledger.job.*</c> metrics. A job that logs an error but returns normally is
/// recorded as a success; jobs that must show up as failed throw.
/// </summary>
public sealed class JobRunRecorder(JobRunHistory history, ILogger<JobRunRecorder> logger, IJobRunStore? store = null)
{
    /// <summary>Persists the run to <c>job_runs</c> (Data page history). A storage failure is logged, never thrown into the scheduler.</summary>
    public async Task PersistAsync(JobRunRecord record, int attempt, CancellationToken ct = default)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            await store.AppendAsync(new JobRunEntry(record.Job, record.StartedUtc, record.StartedUtc + record.Duration, record.Success, record.Error, attempt), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Job {Job}: run history could not be persisted.", record.Job);
        }
    }

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
public sealed class JobRunListener(JobRunRecorder recorder, TimeProvider clock, JobRetryScheduler? retries = null) : IJobListener
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

    public async ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        var finished = clock.GetUtcNow();
        var started = context.FireTimeUtc;
        var record = recorder.Finished(context.JobDetail.Key.Name, started, finished, jobException);
        await recorder.PersistAsync(record, JobRetryScheduler.AttemptOf(context), cancellationToken);
        if (jobException is not null && retries is not null)
        {
            await retries.TryScheduleAsync(context, cancellationToken);
        }
    }

    public ValueTask JobProgressChanged(IJobExecutionContext context, Quartz.Extensibility.FireInstanceProgress progress, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
