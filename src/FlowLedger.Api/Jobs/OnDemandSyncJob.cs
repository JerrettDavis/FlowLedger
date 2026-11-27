using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using Quartz;

namespace FlowLedger.Api.Jobs;

/// <summary>
/// Quartz job that runs a single financial sync on demand. Triggered (enqueued) when a verified
/// MX webhook arrives, so the heavy sync runs in the background and the webhook endpoint can
/// return promptly. Mirrors the Worker's ScheduledSyncJob but is owned by the API host (the API
/// does not reference the Worker assembly).
///
/// The webhook endpoint passes <see cref="MemberIdKey"/> (and optionally <see cref="EventTypeKey"/>)
/// in the <see cref="IJobExecutionContext.MergedJobDataMap"/> so this job can scope the sync to a
/// specific member when targeted per-member sync is supported by <see cref="IFinancialSyncService"/>.
/// Currently <see cref="IFinancialSyncService.SyncAsync"/> syncs all members; the data is forwarded
/// here so that a future targeted-sync overload can be added without changing the webhook endpoint.
/// Webhook-triggered syncs intentionally bypass the manual-refresh cooldown (these are
/// platform-initiated events, not user-initiated refreshes).
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class OnDemandSyncJob : IJob
{
    /// <summary>Quartz job key used to trigger this job from the webhook endpoint.</summary>
    public static readonly JobKey Key = new("OnDemandSyncJob", "FlowLedger");

    /// <summary>JobDataMap key carrying the MX member id that triggered the webhook.</summary>
    public const string MemberIdKey = "memberId";

    /// <summary>JobDataMap key carrying the owning tenant id resolved by the webhook endpoint (#150). Required.</summary>
    public const string TenantIdKey = "tenantId";

    /// <summary>JobDataMap key carrying the MX webhook event type (e.g. "MEMBER_STATUS_UPDATED").</summary>
    public const string EventTypeKey = "eventType";

    private readonly IServiceProvider _services;
    private readonly ILogger<OnDemandSyncJob> _logger;

    public OnDemandSyncJob(IServiceProvider services, ILogger<OnDemandSyncJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OnDemandSyncJob: starting webhook-triggered sync (memberId={MemberId}, eventType={EventType}).")]
    private partial void LogStarting(string? memberId, string? eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OnDemandSyncJob: no tenant in the job data; refusing to sync (memberId={MemberId}).")]
    private partial void LogNoTenant(string? memberId);

    [LoggerMessage(Level = LogLevel.Information, Message = "OnDemandSyncJob: skipped — {Reason}")]
    private partial void LogSkipped(string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "OnDemandSyncJob: complete — {Accounts} accounts, {Added} added, {Skipped} skipped.")]
    private partial void LogComplete(int accounts, int added, int skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "OnDemandSyncJob: throttled; deferring one sync by {Delay}.")]
    private partial void LogThrottled(TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "OnDemandSyncJob: a sync is already running; deferring one more run.")]
    private partial void LogAlreadyRunning();

    [LoggerMessage(Level = LogLevel.Warning, Message = "OnDemandSyncJob: webhook-triggered sync failed ({Kind}): {Message}")]
    private partial void LogSyncFailed(object kind, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "OnDemandSyncJob: webhook-triggered sync faulted.")]
    private partial void LogSyncFaulted(Exception ex);

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var data = context.MergedJobDataMap;
        var memberId = data.ContainsKey(MemberIdKey) ? data.GetString(MemberIdKey) : null;
        var eventType = data.ContainsKey(EventTypeKey) ? data.GetString(EventTypeKey) : null;

        LogStarting(memberId, eventType);

        // The webhook endpoint resolved the owning tenant from the verified member id. Never fall back to the
        // household tenant: without a tenant there is nothing safe to sync.
        if (!data.ContainsKey(TenantIdKey) || !Guid.TryParse(data.GetString(TenantIdKey), out var tenantId))
        {
            LogNoTenant(memberId);
            return;
        }

        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<FlowLedger.Api.Tenancy.TenantOverride>().Use(tenantId);
        // ADR 0018: honour any back-off window shared with the scheduler. A webhook is itself evidence that a connection waiting
        // for the user (or given up on) may have changed, so those states get one attempt (SyncTrigger.ProviderEvent).
        var gate = scope.ServiceProvider.GetService<SyncAttemptGate>();
        if (gate is not null && await gate.CheckAsync(SyncTrigger.ProviderEvent, cancellationToken) is { Allowed: false, Blocking: { } blocked })
        {
            LogSkipped(blocked.Reason);
            return;
        }

        var syncService = scope.ServiceProvider.GetRequiredService<IFinancialSyncService>();

        try
        {
            // TODO: when IFinancialSyncService gains a targeted per-member overload, pass memberId
            // here so only the affected member's accounts/transactions are refreshed.
            var result = await syncService.SyncAsync(cancellationToken);
            LogComplete(result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped);
        }
        catch (SyncThrottledException ex)
        {
            // Not a failure: the minimum-interval guard refused the call. Run once more when the window ends (coalesced).
            LogThrottled(ex.RetryAfter);
            await DeferAsync(context.Scheduler, tenantId, memberId, eventType, ex.RetryAfter, cancellationToken);
        }
        catch (SyncAlreadyRunningException)
        {
            LogAlreadyRunning();
            await DeferAsync(context.Scheduler, tenantId, memberId, eventType, TimeSpan.FromMinutes(1), cancellationToken);
        }
        catch (ProviderException ex)
        {
            // Classified and persisted by the sync service; the shared retry policy decides when it is tried again.
            LogSyncFailed(ProviderIssue.FromException(ex).Kind, ex.Message);
        }
        catch (Exception ex)
        {
            // Swallow-and-log: a webhook-triggered sync failure must not crash the host;
            // the scheduled worker sync remains the durable backstop.
            LogSyncFaulted(ex);
        }
    }

    /// <summary>
    /// Schedules at most ONE deferred run per tenant: later events inside the window coalesce into the pending trigger
    /// (the sync fetches everything, so one run covers them all). Returns true when a new trigger was created.
    /// </summary>
    public static async Task<bool> DeferAsync(
        IScheduler scheduler, Guid tenantId, string? memberId, string? eventType, TimeSpan delay, CancellationToken ct = default)
    {
        var key = new TriggerKey($"deferred-{tenantId:N}", "FlowLedger");
        if (await scheduler.GetTrigger(key, ct) is not null)
        {
            return false;
        }

        var map = new JobDataMap
        {
            [TenantIdKey] = tenantId.ToString(),
            [MemberIdKey] = memberId ?? string.Empty,
            [EventTypeKey] = eventType ?? string.Empty,
        };
        // A dedicated, non-durable job per tenant: it disappears after it fires, and its identity makes the coalescing check trivial.
        var job = JobBuilder.Create<OnDemandSyncJob>().WithIdentity($"deferred-{tenantId:N}", "FlowLedger").Build();
        var trigger = TriggerBuilder.Create()
            .WithIdentity(key)
            .UsingJobData(map)
            .StartAt(DateTimeOffset.UtcNow + (delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(5)) + TimeSpan.FromSeconds(1))
            .Build();
        await scheduler.ScheduleJob(job, trigger, default(ScheduleJobOptions), ct);
        return true;
    }
}
