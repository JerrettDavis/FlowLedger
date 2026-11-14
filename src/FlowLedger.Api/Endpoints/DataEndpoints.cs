using FlowLedger.Infrastructure.DataSafety;

namespace FlowLedger.Api.Endpoints;

/// <summary>Data-safety status for the "Data &amp; backups" page: backup history, off-site state, provider API usage.</summary>
internal static class DataEndpoints
{
    internal static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/data").WithTags("Data");

        group.MapGet("/status", async (
            BackupRunStore store,
            BackupSettings settings,
            NpgsqlProviderCallLedger ledger,
            DataEnvironmentState env,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var runs = await store.RecentAsync(10, ct);
            var lastOk = runs.FirstOrDefault(r => r.Ok);
            var last = runs.FirstOrDefault();
            // Healthy = last run succeeded, was < 36h ago, and (if configured) reached off-site.
            var stale = last is null || !last.Ok || DateTimeOffset.UtcNow - last.FinishedUtc > TimeSpan.FromHours(36);
            var usage = await ledger.GetUsageAsync(MxProvider, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new
            {
                environment = env.Current?.ToString().ToLowerInvariant() ?? "unknown",
                backup = new
                {
                    healthy = !stale,
                    stale,
                    offsiteConfigured = settings.OffsiteConfigured,
                    lastSuccessUtc = lastOk?.FinishedUtc,
                    lastRun = last,
                    runs,
                },
                providerUsage = new
                {
                    provider = MxProvider,
                    usage.CallsToday,
                    usage.CallsThisMonth,
                    dailyBudget = cfg.GetValue("Mx:Provider:DailyCallBudget", 500),
                    monthlyBudget = cfg.GetValue("Mx:Provider:MonthlyCallBudget", 5000),
                    paused = cfg.GetValue("Mx:Provider:CallsPaused", false),
                    usage.ByEndpoint,
                },
            });
        })
        .WithName("GetDataStatus")
        .WithSummary("Backup health, off-site status and provider API usage");

        group.MapPost("/backup-now", async (BackupRunner runner, CancellationToken ct) =>
        {
            var result = await runner.RunAsync(ct);
            return result.Ok ? Results.Ok(result) : Results.Problem(title: "Backup failed", detail: result.Error, statusCode: 500);
        })
        .WithName("BackupNow")
        .WithSummary("Run a verified backup (snapshot, restore drill, off-site copy) now")
        .RequireRateLimiting("write");

        return app;
    }

    private const string MxProvider = "MX";
}
