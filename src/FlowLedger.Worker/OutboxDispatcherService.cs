using FlowLedger.Infrastructure.Events;

namespace FlowLedger.Worker;

/// <summary>
/// Polls the transactional outbox (#92) and delivers committed domain events to their handlers, once per tenant in that tenant's
/// own scope. Handler failures are retried with backoff and dead-lettered by <see cref="OutboxProcessor"/>; this loop never exits
/// on a failure, it logs and polls again.
/// </summary>
internal sealed partial class OutboxDispatcherService(IServiceProvider services, OutboxOptions options, ILogger<OutboxDispatcherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)));
        do
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDispatchFailed(ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One pass over every tenant, draining each tenant's due messages.</summary>
    public Task DrainOnceAsync(CancellationToken ct) =>
        TenantFanOut.ForEachTenantAsync(services, logger, async (sp, _) =>
        {
            var processor = sp.GetRequiredService<OutboxProcessor>();
            while (await processor.ProcessBatchAsync(ct) >= options.BatchSize)
            {
            }
        }, ct);

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch pass failed; will retry on the next poll.")]
    private partial void LogDispatchFailed(Exception exception);
}
