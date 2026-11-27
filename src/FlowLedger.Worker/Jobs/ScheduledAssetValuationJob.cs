using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Domain.Aggregates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Jobs;

/// <summary>
/// Monthly refresh of real-estate values from the configured appraisal feed (AssetFeeds:Http, e.g. a property AVM).
/// One call per property per run keeps usage inside free API tiers. Failures are logged, never thrown: a missing
/// or disabled feed simply leaves the last recorded value (and the model estimate) in place.
///
/// Config: <c>AssetValuation:AutoRefresh:Enabled</c> (default true), <c>:CronExpression</c> (default 1st of month 04:00),
/// <c>:FeedName</c> (default: the AssetFeeds:Http:Name feed, "appraiser").
/// </summary>
[DisallowConcurrentExecution]
internal sealed class ScheduledAssetValuationJob(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<ScheduledAssetValuationJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var feedName = configuration["AssetValuation:AutoRefresh:FeedName"]
                       ?? configuration["AssetFeeds:Http:Name"]
                       ?? "appraiser";

        await FlowLedger.Worker.TenantFanOut.ForEachTenantAsync(services, logger, async (sp, _) =>
        {
            var assets = sp.GetRequiredService<IAssetRepository>();
            var refresh = sp.GetRequiredService<RefreshAssetValuationHandler>();

            IReadOnlyList<Asset> all;
            try
            {
                all = await assets.ListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ScheduledAssetValuationJob: could not list assets.");
                return;
            }

            var homes = all.Where(a => a.IsActive && a.Category == AssetCategory.RealEstate).ToList();
            logger.LogInformation("ScheduledAssetValuationJob: refreshing {Count} real-estate asset(s) from '{Feed}'.", homes.Count, feedName);

            foreach (var home in homes)
            {
                try
                {
                    var result = await refresh.HandleAsync(home.Id, feedName, cancellationToken);
                    logger.LogInformation("ScheduledAssetValuationJob: {Message}", result?.Message ?? "asset not found");
                }
                catch (Exception ex)
                {
                    // e.g. feed disabled / not configured, network error, rate limit.
                    logger.LogWarning(ex, "ScheduledAssetValuationJob: refresh failed for one asset; keeping last value.");
                }
            }
        }, cancellationToken);
    }
}
