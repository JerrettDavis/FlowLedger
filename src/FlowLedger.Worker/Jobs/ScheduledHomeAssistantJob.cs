using FlowLedger.Application.Features.HomeAssistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Jobs;

/// <summary>
/// Pushes new alert events (bills, paychecks, overdraft, budgets) to each household's Home Assistant, if they enabled it.
/// Households that have not turned sharing on are a cheap no-op. A failing tenant never blocks the others.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ScheduledHomeAssistantJob(IServiceProvider services, ILogger<ScheduledHomeAssistantJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await TenantFanOut.ForEachTenantAsync(services, logger, async (sp, tenantId) =>
        {
            var result = await sp.GetRequiredService<HomeAssistantService>()
                .RunAsync(DateOnly.FromDateTime(DateTime.Now), cancellationToken);
            if (result.Sent > 0 || result.Failed > 0)
            {
                logger.LogInformation("Home Assistant: tenant {TenantId} sent {Sent}, failed {Failed}, already sent {Already}.",
                    tenantId, result.Sent, result.Failed, result.AlreadySent);
            }
        }, cancellationToken);
    }
}
