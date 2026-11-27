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
public sealed partial class JobRunRecorder(JobRunHistory history, ILogger<JobRunRecorder> logger, IJobRunStore? store = null)
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
            LogHistoryNotPersisted(ex, record.Job);
        }
    }

    public void Started(string job, DateTimeOffset scheduledUtc)
        => LogStarting(job, scheduledUtc);

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
            LogFinishedOk(job, duration.TotalSeconds);
        }
        else
        {
            LogFailed(failure, job, duration.TotalSeconds, error);
        }

        return record;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job}: run history could not be persisted.")]
    private partial void LogHistoryNotPersisted(Exception exception, string job);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} starting (scheduled {Scheduled:o}).")]
    private partial void LogStarting(string job, DateTimeOffset scheduled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} finished OK in {Duration:F1}s.")]
    private partial void LogFinishedOk(string job, double duration);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job} FAILED after {Duration:F1}s: {Error}")]
    private partial void LogFailed(Exception? exception, string job, double duration, string? error);
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
