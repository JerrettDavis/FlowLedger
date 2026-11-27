using FlowLedger.Infrastructure.DataSafety;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Scheduling;

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
/// finished. Fails open: if the marks table is unreachable the job runs rather than silently never running.
/// </summary>
internal sealed partial class JobFireGate(ILogger<JobFireGate> logger, IJobFireMarks? marks = null)
{
    public async Task<bool> MayFireAsync(ITrigger trigger, IJobExecutionContext context, CancellationToken ct = default)
    {
        // A retry re-runs a slot that was already claimed by its first attempt.
        if (marks is null || JobRetryScheduler.AttemptOf(context) > 1 || JobFireSlots.SlotOf(trigger, context) is not { } slot)
        {
            return true;
        }

        var job = context.JobDetail.Key.Name;
        try
        {
            if (await marks.TryClaimAsync(job, slot, ct))
            {
                return true;
            }

            LogSlotAlreadyClaimed(job, slot);
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogMarksUnavailable(ex, job);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} skipped: fire time {Slot:o} was already claimed (another replica, or this one before a restart).")]
    private partial void LogSlotAlreadyClaimed(string job, DateTimeOffset slot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job}: could not record its fire time; running without the once-per-fire-time guard.")]
    private partial void LogMarksUnavailable(Exception exception, string job);
}

/// <summary>
/// At startup, runs each cron job ONCE if a fire time passed while no worker was up (#187): the in-memory Quartz store forgets missed
/// fires on restart. The first start on a database only records the current slot. Never delays or fails startup.
/// </summary>
internal sealed partial class JobCatchUpService(
    ISchedulerFactory schedulers, ILogger<JobCatchUpService> logger, TimeProvider clock, Microsoft.Extensions.Configuration.IConfiguration configuration,
    IJobFireMarks? marks = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (marks is null || !configuration.GetValue("Worker:CatchUp:Enabled", true))
        {
            return;
        }

        try
        {
            var scheduler = await schedulers.GetScheduler(cancellationToken);
            foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("FlowLedger"), cancellationToken))
            {
                var cron = (await scheduler.GetTriggersOfJob(key, cancellationToken)).OfType<ICronTrigger>().FirstOrDefault();
                if (cron is null || JobFireSlots.SlotAtOrBefore(cron, clock.GetUtcNow()) is not { } due)
                {
                    continue; // disabled job, or no fire time yet
                }

                var last = await marks.LastFireAsync(key.Name, cancellationToken);
                if (last is null)
                {
                    await marks.SeedAsync(key.Name, due, cancellationToken);
                    LogSeeded(key.Name, due);
                }
                else if (last < due)
                {
                    // One run for the newest missed slot, never a burst for every slot missed; the gate claims the slot it names.
                    await scheduler.TriggerJob(key, new JobDataMap { [JobFireSlots.CatchUpKey] = due.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
                    LogCatchingUp(key.Name, last.Value, due);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatchUpFailed(ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job}: first start on this database; recorded fire time {Slot:o} without running it.")]
    private partial void LogSeeded(string job, DateTimeOffset slot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job} missed a fire time while no worker was running (last claimed {Last:o}); running once for {Due:o}.")]
    private partial void LogCatchingUp(string job, DateTimeOffset last, DateTimeOffset due);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Missed-fire catch-up could not run; scheduled jobs continue on their normal schedule.")]
    private partial void LogCatchUpFailed(Exception exception);
}
