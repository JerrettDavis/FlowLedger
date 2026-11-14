using System.Net;
using FlowLedger.Infrastructure.DataSafety;

namespace FlowLedger.Api.Endpoints;

/// <summary>Data-safety status for the "Data &amp; backups" page: backup history, off-site state, provider API usage.</summary>
internal static class DataEndpoints
{
    internal static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/data").WithTags("Data").RequireRateLimiting("api");

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
                    lastRun = last is null ? null : Public(last),
                    runs = runs.Select(Public).ToList(),
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
            if (result.Error == "A backup is already running.")
            {
                return Results.Problem(title: "Backup already running", statusCode: StatusCodes.Status409Conflict);
            }

            return result.Ok ? Results.Ok(Public(result)) : Results.Problem(title: "Backup failed", detail: result.Error, statusCode: 500);
        })
        .WithName("BackupNow")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Run a verified backup (snapshot, restore drill, off-site copy) now")
        .RequireRateLimiting("write");

        group.MapGet("/export", async (IConfiguration cfg, BackupSettings settings, CancellationToken ct) =>
        {
            var conn = cfg.GetConnectionString("flowledger")!;
            var zip = await SnapshotImport.ExportZipAsync(conn, Path.Combine(settings.BackupRoot, "exports"), ct);
            // DeleteOnClose: the unencrypted export zip is removed from the server as soon as it has been sent.
            var stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return Results.File(stream, "application/zip", $"flowledger-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        })
        .WithName("ExportData")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Download ALL data as a portable zip (CSV per table + manifest with hashes)")
        .RequireRateLimiting("write");

        group.MapPost("/import", async (HttpRequest request, IConfiguration cfg, BackupSettings settings, CancellationToken ct) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count != 1)
            {
                return Results.Problem(title: "Upload one export .zip as multipart form field 'file'.", statusCode: 400);
            }

            var conn = cfg.GetConnectionString("flowledger")!;
            await using var upload = request.Form.Files[0].OpenReadStream();
            var result = await SnapshotImport.ImportZipAsync(conn, upload, Path.Combine(settings.BackupRoot, "imports"), ct);
            return result.Ok ? Results.Ok(result) : Results.Problem(title: "Import refused or failed", detail: string.Join("; ", result.Problems), statusCode: 409);
        })
        .DisableAntiforgery()
        .WithName("ImportData")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Import an export zip into an EMPTY database (disaster recovery / moving machines)")
        .RequireRateLimiting("write");

        return app;
    }

    private const string MxProvider = "MX";

    /// <summary>
    /// Export, import and backup-now act on the WHOLE database (every tenant). They are therefore reachable only from
    /// the machine itself unless <c>DataAdmin:AllowRemote=true</c> is set deliberately (e.g. Docker bridge networking).
    /// </summary>
    private static async ValueTask<object?> LoopbackOnlyAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var allowRemote = http.RequestServices.GetRequiredService<IConfiguration>().GetValue("DataAdmin:AllowRemote", false);
        var remote = http.Connection.RemoteIpAddress;
        if (!allowRemote && remote is not null && !IPAddress.IsLoopback(remote))
        {
            return Results.Problem(
                title: "Data administration is only available from the host itself",
                detail: "Set DataAdmin:AllowRemote=true to allow it from other addresses.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(ctx);
    }

    /// <summary>Response projection: the snapshot folder name only (never the absolute server path).</summary>
    private static object Public(BackupRunResult r) => new
    {
        r.Ok,
        r.StartedUtc,
        r.FinishedUtc,
        SnapshotDir = r.SnapshotDir is null ? null : Path.GetFileName(r.SnapshotDir),
        r.Rows,
        r.OffsiteOk,
        r.OffsiteDetail,
        r.DrillOk,
        r.Error,
    };
}
