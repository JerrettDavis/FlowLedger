using System.Collections.Concurrent;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Scheduling;

/// <summary>
/// Cross-process exclusion per job. <c>[DisallowConcurrentExecution]</c> only guards one scheduler; during a rolling
/// deploy or with an accidental second worker, two processes would run the same job. This holds a Postgres advisory
/// lock (<c>flowledger:job:&lt;name&gt;</c>) for the length of a run. The lock prevents OVERLAP; <see cref="JobFireGate"/> (the
/// persisted last-fire mark) additionally stops a second run of the same fire time that starts after the first finished.
/// </summary>
internal sealed partial class JobExclusion(IConfiguration configuration, ILogger<JobExclusion> logger)
{
    private readonly ConcurrentDictionary<string, PostgresAdvisoryLock> _held = new();

    public static string LockName(string job) => $"flowledger:job:{job}";

    /// <returns>True when the run may proceed (lock held, or exclusion unavailable and logged).</returns>
    public async Task<bool> TryEnterAsync(string job, string fireInstanceId, CancellationToken ct = default)
    {
        var cs = configuration.GetConnectionString("flowledger");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return true;
        }

        try
        {
            var handle = await PostgresAdvisoryLock.TryAcquireAsync(cs, LockName(job), ct);
            if (handle is null)
            {
                LogSkippedLockHeld(job);
                return false;
            }

            _held[fireInstanceId] = handle;
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Database trouble must not silently stop scheduled work; the jobs themselves report their own DB errors.
            LogLockUnavailable(ex, job);
            return true;
        }
    }

    public async Task ExitAsync(string fireInstanceId)
    {
        if (_held.TryRemove(fireInstanceId, out var handle))
        {
            await handle.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job} skipped: another process holds its lock (overlapping worker or deploy).")]
    private partial void LogSkippedLockHeld(string job);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job}: could not take the cross-process lock; running without it.")]
    private partial void LogLockUnavailable(Exception exception, string job);
}

/// <summary>Quartz adapter: takes the lock in <c>VetoJobExecution</c> and releases it in <c>TriggerComplete</c>.</summary>
internal sealed class JobExclusionTriggerListener(JobExclusion exclusion, JobFireGate? gate = null) : ITriggerListener
{
    public string Name => nameof(JobExclusionTriggerListener);

    public ValueTask TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async ValueTask<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var proceed = await exclusion.TryEnterAsync(context.JobDetail.Key.Name, context.FireInstanceId, cancellationToken);

        // Holding the lock, claim this fire time: a replica that fires the same slot after the first one finished is vetoed here (#187).
        if (proceed && gate is not null && !await gate.MayFireAsync(trigger, context, cancellationToken))
        {
            await exclusion.ExitAsync(context.FireInstanceId); // a vetoed firing never reaches TriggerComplete
            proceed = false;
        }

        if (!proceed)
        {
            FlowLedgerTelemetry.RecordJobVetoed(context.JobDetail.Key.Name);
        }

        return !proceed;
    }

    public ValueTask TriggerMisfired(ITrigger trigger, IScheduler scheduler, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask TriggerSkipped(ITrigger trigger, IScheduler scheduler, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async ValueTask TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction triggerInstructionCode, CancellationToken cancellationToken = default)
        => await exclusion.ExitAsync(context.FireInstanceId);

    public ValueTask TriggerRetriesExhausted(ITrigger trigger, IJobExecutionContext context, JobExecutionException exception, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
