namespace FlowLedger.Worker;

/// <summary>
/// Placeholder background worker. Replaced by Quartz-scheduled jobs in Milestone 2.
/// </summary>
public sealed class PlaceholderWorker(ILogger<PlaceholderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FlowLedger worker host started. Awaiting job registration in Milestone 2.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
