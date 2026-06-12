using FlowLedger.Application.Abstractions;
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
public sealed class OnDemandSyncJob : IJob
{
    /// <summary>Quartz job key used to trigger this job from the webhook endpoint.</summary>
    public static readonly JobKey Key = new("OnDemandSyncJob", "FlowLedger");

    /// <summary>JobDataMap key carrying the MX member id that triggered the webhook.</summary>
    public const string MemberIdKey = "memberId";

    /// <summary>JobDataMap key carrying the MX webhook event type (e.g. "MEMBER_STATUS_UPDATED").</summary>
    public const string EventTypeKey = "eventType";

    private readonly IServiceProvider _services;
    private readonly ILogger<OnDemandSyncJob> _logger;

    public OnDemandSyncJob(IServiceProvider services, ILogger<OnDemandSyncJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        var memberId = data.ContainsKey(MemberIdKey) ? data.GetString(MemberIdKey) : null;
        var eventType = data.ContainsKey(EventTypeKey) ? data.GetString(EventTypeKey) : null;

        _logger.LogInformation(
            "OnDemandSyncJob: starting webhook-triggered sync (memberId={MemberId}, eventType={EventType}).",
            memberId, eventType);

        await using var scope = _services.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IFinancialSyncService>();

        try
        {
            // TODO: when IFinancialSyncService gains a targeted per-member overload, pass memberId
            // here so only the affected member's accounts/transactions are refreshed.
            var result = await syncService.SyncAsync(context.CancellationToken);
            _logger.LogInformation(
                "OnDemandSyncJob: complete — {Accounts} accounts, {Added} added, {Skipped} skipped.",
                result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped);
        }
        catch (Exception ex)
        {
            // Swallow-and-log: a webhook-triggered sync failure must not crash the host;
            // the scheduled worker sync remains the durable backstop.
            _logger.LogError(ex, "OnDemandSyncJob: webhook-triggered sync faulted.");
        }
    }
}
