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
/// lock (<c>flowledger:job:&lt;name&gt;</c>) for the length of a run. It prevents OVERLAP, not a second run
/// that starts after the first finished (that needs a persisted last-fire marker).
/// </summary>
internal sealed class JobExclusion(IConfiguration configuration, ILogger<JobExclusion> logger)
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
                logger.LogWarning("Job {Job} skipped: another process holds its lock (overlapping worker or deploy).", job);
                return false;
            }

            _held[fireInstanceId] = handle;
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Database trouble must not silently stop scheduled work; the jobs themselves report their own DB errors.
            logger.LogWarning(ex, "Job {Job}: could not take the cross-process lock; running without it.", job);
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
}

/// <summary>Quartz adapter: takes the lock in <c>VetoJobExecution</c> and releases it in <c>TriggerComplete</c>.</summary>
internal sealed class JobExclusionTriggerListener(JobExclusion exclusion) : ITriggerListener
{
    public string Name => nameof(JobExclusionTriggerListener);

    public ValueTask TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async ValueTask<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var proceed = await exclusion.TryEnterAsync(context.JobDetail.Key.Name, context.FireInstanceId, cancellationToken);
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

    public ValueTask TriggerRetriesExhausted(ITrigger trigger, IJobExecutionContext context, JobExecutionException jobException, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
