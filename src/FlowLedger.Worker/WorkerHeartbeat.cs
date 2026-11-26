using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker;

/// <summary>
/// Health surface for the Worker (a generic host with no HTTP listener): while the Quartz scheduler is running, a
/// timestamp file is refreshed every <see cref="Interval"/>. An orchestrator probes it with
/// <c>dotnet FlowLedger.Worker.dll --healthcheck</c> (exit 0 healthy, 1 stale/missing), which works in the chiseled
/// image that has no shell or curl. A wedged or stopped scheduler stops refreshing the file and the probe fails.
/// </summary>
public sealed class WorkerHeartbeat(ISchedulerFactory schedulers, IConfiguration configuration, ILogger<WorkerHeartbeat> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    public static string PathFrom(IConfiguration configuration) =>
        configuration["Worker:HeartbeatFile"] is { Length: > 0 } p
            ? p
            : Path.Combine(Path.GetTempPath(), "flowledger-worker.heartbeat");

    /// <summary>True when the file exists and was refreshed within <paramref name="maxAge"/>.</summary>
    public static bool IsHealthy(string path, TimeSpan maxAge, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path) || !DateTimeOffset.TryParse(File.ReadAllText(path).Trim(), out var stamp))
            {
                return false;
            }

            return now - stamp <= maxAge;
        }
        catch (IOException)
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = PathFrom(configuration);
        while (!stoppingToken.IsCancellationRequested)
        {
            var wrote = false;
            try
            {
                var scheduler = await schedulers.GetScheduler(stoppingToken);
                if (scheduler.Status == SchedulerStatus.Running)
                {
                    await File.WriteAllTextAsync(path, DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
                    wrote = true;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Worker heartbeat could not be written to {Path}.", path);
            }

            try
            {
                // Retry quickly until the scheduler is up, then settle into the normal cadence.
                await Task.Delay(wrote ? Interval : TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
