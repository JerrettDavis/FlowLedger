using FlowLedger.Application.Abstractions;
using Quartz;

namespace FlowLedger.Api.Jobs;

/// <summary>
/// Quartz job that runs a single financial sync on demand. Triggered (enqueued) when a verified
/// MX webhook arrives, so the heavy sync runs in the background and the webhook endpoint can
/// return promptly. Mirrors the Worker's ScheduledSyncJob but is owned by the API host (the API
/// does not reference the Worker assembly).
/// </summary>
[DisallowConcurrentExecution]
public sealed class OnDemandSyncJob : IJob
{
    /// <summary>Quartz job key used to trigger this job from the webhook endpoint.</summary>
    public static readonly JobKey Key = new("OnDemandSyncJob", "FlowLedger");

    private readonly IServiceProvider _services;
    private readonly ILogger<OnDemandSyncJob> _logger;

    public OnDemandSyncJob(IServiceProvider services, ILogger<OnDemandSyncJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("OnDemandSyncJob: starting webhook-triggered sync.");

        await using var scope = _services.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IFinancialSyncService>();

        try
        {
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
