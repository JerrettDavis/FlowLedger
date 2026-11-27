using System.Collections.Concurrent;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Scheduling;

/// <summary>What a job does when its fire-time marks cannot be reached: run (risking a duplicate) or skip (risking a late run).</summary>
internal enum FireGateFailureMode
{
    /// <summary>Fail closed: skip this firing; the next catch-up check re-fires it once the table is back. The default, because every job here has external side effects.</summary>
    Skip,

    /// <summary>Fail open: run anyway. A duplicate run is possible if another replica also could not reach the table.</summary>
    Run,
}

/// <summary>Bound from <c>Worker:FireGate</c>.</summary>
/// <param name="OnStoreFailure"><c>Worker:FireGate:OnStoreFailure</c> = Skip (default) | Run; <c>Worker:FireGate:Jobs:&lt;JobName&gt;:OnStoreFailure</c> overrides it per job.</param>
/// <param name="Attempts">Claim attempts before the store counts as down (default 3).</param>
/// <param name="RetryDelay">Pause between attempts (default 250 ms).</param>
/// <param name="Lease">How long an unconfirmed claim is honoured before another replica may take the slot over (default 10 minutes).</param>
/// <param name="PerJob">Per-job overrides of <paramref name="OnStoreFailure"/>.</param>
internal sealed record JobFireGateOptions(
    FireGateFailureMode OnStoreFailure, int Attempts, TimeSpan RetryDelay, TimeSpan Lease, IReadOnlyDictionary<string, FireGateFailureMode> PerJob)
{
    public static JobFireGateOptions Default { get; } = new(
        FireGateFailureMode.Skip, 3, TimeSpan.FromMilliseconds(250), TimeSpan.FromMinutes(10), new Dictionary<string, FireGateFailureMode>());

    public static JobFireGateOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Worker:FireGate");
        var perJob = new Dictionary<string, FireGateFailureMode>(StringComparer.Ordinal);
        foreach (var job in section.GetSection("Jobs").GetChildren())
        {
            if (Enum.TryParse<FireGateFailureMode>(job["OnStoreFailure"], ignoreCase: true, out var mode))
            {
                perJob[job.Key] = mode;
            }
        }

        return new JobFireGateOptions(
            Enum.TryParse<FireGateFailureMode>(section["OnStoreFailure"], ignoreCase: true, out var global) ? global : FireGateFailureMode.Skip,
            Math.Clamp(section.GetValue("Attempts", 3), 1, 10),
            TimeSpan.FromMilliseconds(Math.Clamp(section.GetValue("RetryDelayMs", 250), 0, 10_000)),
            TimeSpan.FromSeconds(Math.Clamp(section.GetValue("LeaseSeconds", 600), 5, 86_400)),
            perJob);
    }

    public FireGateFailureMode ModeFor(string job) => PerJob.TryGetValue(job, out var mode) ? mode : OnStoreFailure;
}

/// <summary>Maps a firing to the cron slot it belongs to, so every replica names one fire time the same way (#187).</summary>
internal static class JobFireSlots
{
    /// <summary>JobDataMap key a startup catch-up trigger carries: the missed slot it stands in for (round-trip format).</summary>
    public const string CatchUpKey = "catchUpFireUtc";

    /// <summary>The newest cron fire time at or before <paramref name="instant"/>, or null when the expression has none.</summary>
    public static DateTimeOffset? SlotAtOrBefore(ICronTrigger trigger, DateTimeOffset instant)
    {
        // A fresh CronExpression, not the trigger's own fire times: a trigger refuses to produce times before its start (the moment this
        // process registered it), which are exactly the slots a restart needs to look back over.
        var expression = CronExpression.Parse(trigger.CronExpressionString!).WithTimeZone(trigger.TimeZone);
        // Slots fall on whole seconds; "strictly before the next whole second" is "at or before now".
        var wholeSecond = instant - TimeSpan.FromTicks(instant.Ticks % TimeSpan.TicksPerSecond);
        return expression.GetPreviousValidTimeBefore(wholeSecond.AddSeconds(1));
    }

    /// <summary>
    /// The slot this firing claims: the catch-up trigger's stated slot, else the newest cron slot at or before the scheduled time (a
    /// misfire fired late by FireAndProceed still maps to the slot it was due for), else null (one-off triggers are not slots).
    /// </summary>
    public static DateTimeOffset? SlotOf(ITrigger trigger, IJobExecutionContext context)
    {
        if (context.MergedJobDataMap.TryGetValue(CatchUpKey, out var stated)
            && DateTimeOffset.TryParse(stated?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var slot))
        {
            return slot.ToUniversalTime();
        }

        return trigger is ICronTrigger cron ? SlotAtOrBefore(cron, context.ScheduledFireTimeUtc ?? context.FireTimeUtc) : null;
    }
}

/// <summary>
/// Once per fire time across replicas (#187): the first process to claim a cron slot runs it; any other process that fires the same slot
/// is vetoed. Complements the advisory lock, which only stops two runs overlapping and cannot stop a second one starting after the first
/// finished. The claim is a lease confirmed when the run ends (<see cref="CompleteAsync"/>); a claim whose owner died stays unconfirmed
/// and is taken over once the lease runs out, by the catch-up check. If the marks table cannot be reached after a bounded number of
/// attempts the job is SKIPPED by default (fail closed; <see cref="JobFireGateOptions"/>), and the decision is counted on
/// <c>flowledger.job.firegate</c>.
/// </summary>
internal sealed partial class JobFireGate(
    ILogger<JobFireGate> logger, IJobFireMarks? marks = null, JobFireGateOptions? options = null, TimeProvider? clock = null)
{
    private readonly JobFireGateOptions _options = options ?? JobFireGateOptions.Default;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _claimed = new(StringComparer.Ordinal);

    /// <summary>One id per worker process: the claim's owner.</summary>
    public string Owner { get; } = "worker-" + Guid.NewGuid().ToString("N");

    public TimeSpan Lease => _options.Lease;

    public async Task<bool> MayFireAsync(ITrigger trigger, IJobExecutionContext context, CancellationToken ct = default)
    {
        // A retry re-runs a slot that was already claimed by its first attempt.
        if (marks is null || JobRetryScheduler.AttemptOf(context) > 1 || JobFireSlots.SlotOf(trigger, context) is not { } slot)
        {
            return true;
        }

        var job = context.JobDetail.Key.Name;
        Exception? failure = null;
        for (var attempt = 1; attempt <= _options.Attempts; attempt++)
        {
            try
            {
                if (await marks.TryClaimAsync(job, slot, Owner, _options.Lease, ct))
                {
                    _claimed[context.FireInstanceId] = slot;
                    FlowLedgerTelemetry.RecordJobFireGate(job, "claimed");
                    return true;
                }

                FlowLedgerTelemetry.RecordJobFireGate(job, "duplicate");
                LogSlotAlreadyClaimed(job, slot);
                return false;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failure = ex;
                if (attempt < _options.Attempts && _options.RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.RetryDelay, clock ?? TimeProvider.System, ct);
                }
            }
        }

        if (_options.ModeFor(job) == FireGateFailureMode.Run)
        {
            FlowLedgerTelemetry.RecordJobFireGate(job, "store_failed_ran");
            LogMarksUnavailableRunning(failure, job, _options.Attempts);
            return true;
        }

        FlowLedgerTelemetry.RecordJobFireGate(job, "store_failed_skipped");
        LogMarksUnavailableSkipping(failure, job, _options.Attempts);
        return false;
    }

    /// <summary>Confirms the claim this firing took (called when the run ends, whatever its outcome); an unconfirmed claim is re-fired after its lease.</summary>
    public async Task CompleteAsync(IJobExecutionContext context, CancellationToken ct = default)
    {
        if (marks is null || !_claimed.TryRemove(context.FireInstanceId, out var slot))
        {
            return;
        }

        var job = context.JobDetail.Key.Name;
        for (var attempt = 1; attempt <= _options.Attempts; attempt++)
        {
            try
            {
                if (!await marks.CompleteAsync(job, slot, Owner, ct))
                {
                    LogClaimNoLongerOurs(job, slot);
                }

                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt == _options.Attempts)
                {
                    FlowLedgerTelemetry.RecordJobFireGate(job, "complete_failed");
                    LogCompleteFailed(ex, job, slot);
                }
                else if (_options.RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.RetryDelay, clock ?? TimeProvider.System, ct);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} skipped: fire time {Slot:o} was already claimed (another replica, or this one before a restart).")]
    private partial void LogSlotAlreadyClaimed(string job, DateTimeOffset slot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job}: the fire-time marks were unreachable after {Attempts} attempts; running anyway (Worker:FireGate:OnStoreFailure=Run), so a duplicate run is possible.")]
    private partial void LogMarksUnavailableRunning(Exception? exception, string job, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job}: the fire-time marks were unreachable after {Attempts} attempts; SKIPPING this firing (fail closed). The next catch-up check re-fires it once the database is back.")]
    private partial void LogMarksUnavailableSkipping(Exception? exception, string job, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job}: fire time {Slot:o} was taken over by another replica before this run confirmed it (its lease ran out).")]
    private partial void LogClaimNoLongerOurs(string job, DateTimeOffset slot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job}: could not confirm fire time {Slot:o}; it will look abandoned and may be re-fired after the lease.")]
    private partial void LogCompleteFailed(Exception exception, string job, DateTimeOffset slot);
}

/// <summary>
/// Runs each cron job ONCE when a fire time was missed (#187): at startup (the in-memory Quartz store forgets missed fires on restart) and
/// then on a timer, which also re-fires a slot whose claim was never confirmed because its owner died (after the lease ran out). The
/// first start on a database only records the current slot. Never delays or fails startup.
/// </summary>
internal sealed partial class JobCatchUpService(
    ISchedulerFactory schedulers, ILogger<JobCatchUpService> logger, TimeProvider clock, IConfiguration configuration, JobFireGate gate,
    IJobFireMarks? marks = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (marks is null || !configuration.GetValue("Worker:CatchUp:Enabled", true))
        {
            return;
        }

        await CheckAsync(stoppingToken);
        var interval = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Worker:CatchUp:IntervalMinutes", 5), 1, 1440));
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CheckAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One pass over the cron jobs; never throws.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (marks is null)
        {
            return;
        }

        try
        {
            var scheduler = await schedulers.GetScheduler(cancellationToken);
            foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("FlowLedger"), cancellationToken))
            {
                var cron = (await scheduler.GetTriggersOfJob(key, cancellationToken)).OfType<ICronTrigger>().FirstOrDefault();
                var now = clock.GetUtcNow();
                if (cron is null || JobFireSlots.SlotAtOrBefore(cron, now) is not { } due)
                {
                    continue; // disabled job, or no fire time yet
                }

                var mark = await marks.GetAsync(key.Name, cancellationToken);
                if (mark is null)
                {
                    await marks.SeedAsync(key.Name, due, cancellationToken);
                    LogSeeded(key.Name, due);
                }
                else if (mark.LastFireUtc < due)
                {
                    await RefireAsync(scheduler, key, due, $"missed (last claimed {mark.LastFireUtc:o})", cancellationToken);
                }
                else if (mark.LastFireUtc == due && mark.CompletedUtc is null && mark.ClaimedUtc <= now - gate.Lease)
                {
                    await RefireAsync(scheduler, key, due, $"abandoned (claimed {mark.ClaimedUtc:o} by {mark.Owner} and never confirmed)", cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatchUpFailed(ex);
        }
    }

    private async Task RefireAsync(IScheduler scheduler, JobKey key, DateTimeOffset due, string why, CancellationToken ct)
    {
        // One run for the newest slot, never a burst for every slot missed; the gate claims the slot it names.
        await scheduler.TriggerJob(key, new JobDataMap { [JobFireSlots.CatchUpKey] = due.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }, ct);
        LogRefiring(key.Name, due, why);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job}: first start on this database; recorded fire time {Slot:o} without running it.")]
    private partial void LogSeeded(string job, DateTimeOffset slot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job}: fire time {Due:o} is {Why}; running it once.")]
    private partial void LogRefiring(string job, DateTimeOffset due, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Missed-fire catch-up could not run; scheduled jobs continue on their normal schedule.")]
    private partial void LogCatchUpFailed(Exception exception);
}
