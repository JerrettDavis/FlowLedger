using FlowLedger.Application.Features.Alerts;
using FlowLedger.Application.Features.HomeAssistant;
using Quartz;

namespace FlowLedger.Worker.Jobs;

/// <summary>
/// Pushes new alert events (bills, paychecks, overdraft, budgets) to each household's Home Assistant, if they enabled it.
/// Also delivers them to webhooks subscribed to alert.raised.
/// Households that have not turned sharing on are a cheap no-op. A failing tenant never blocks the others.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class ScheduledHomeAssistantJob(IServiceProvider services, ILogger<ScheduledHomeAssistantJob> logger) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        => FlowLedger.Worker.Scheduling.JobTrace.RunAsync(context, logger, ct => ExecuteCoreAsync(context, ct), cancellationToken);

    private async ValueTask ExecuteCoreAsync(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await TenantFanOut.ForEachTenantAsync(services, logger, async (sp, tenantId) =>
        {
            var result = await sp.GetRequiredService<HomeAssistantService>()
                .RunAsync(cancellationToken);
            if (result.Sent > 0 || result.Failed > 0)
            {
                LogRunResult(tenantId, result.Sent, result.Failed, result.AlreadySent);
            }

            // The same alerts also go to webhooks subscribed to alert.raised (a no-op without one).
            var delivery = await sp.GetRequiredService<AlertDeliveryService>().RunAsync(cancellationToken);
            if (delivery.Sent > 0 || delivery.Failed > 0)
            {
                LogWebhookDelivery(tenantId, delivery.Sent, delivery.Failed, delivery.AlreadySent);
            }
        }, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert webhooks: tenant {TenantId} sent {Sent}, failed {Failed}, already sent {Already}.")]
    private partial void LogWebhookDelivery(Guid tenantId, int sent, int failed, int already);

    [LoggerMessage(Level = LogLevel.Information, Message = "Home Assistant: tenant {TenantId} sent {Sent}, failed {Failed}, already sent {Already}.")]
    private partial void LogRunResult(Guid tenantId, int sent, int failed, int already);
}
