using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Domain.Aggregates;
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
internal sealed partial class ScheduledAssetValuationJob(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<ScheduledAssetValuationJob> logger) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => FlowLedger.Worker.Scheduling.JobTrace.RunAsync(context, logger, ct => ExecuteCoreAsync(context, ct), cancellationToken);

    private async ValueTask ExecuteCoreAsync(IJobExecutionContext context, CancellationToken cancellationToken)
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
                LogCouldNotListAssets(ex);
                return;
            }

            var homes = all.Where(a => a.IsActive && a.Category == AssetCategory.RealEstate).ToList();
            LogRefreshing(homes.Count, feedName);

            foreach (var home in homes)
            {
                try
                {
                    var result = await refresh.HandleAsync(home.Id, feedName, cancellationToken);
                    LogResult(result?.Message ?? "asset not found");
                }
                catch (Exception ex)
                {
                    // e.g. feed disabled / not configured, network error, rate limit.
                    LogRefreshFailed(ex);
                }
            }
        }, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "ScheduledAssetValuationJob: could not list assets.")]
    private partial void LogCouldNotListAssets(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "ScheduledAssetValuationJob: refreshing {Count} real-estate asset(s) from '{Feed}'.")]
    private partial void LogRefreshing(int count, string feed);

    [LoggerMessage(Level = LogLevel.Information, Message = "ScheduledAssetValuationJob: {Message}")]
    private partial void LogResult(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ScheduledAssetValuationJob: refresh failed for one asset; keeping last value.")]
    private partial void LogRefreshFailed(Exception exception);
}
