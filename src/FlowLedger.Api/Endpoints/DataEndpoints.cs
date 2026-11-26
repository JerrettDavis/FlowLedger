using FlowLedger.Infrastructure.DataSafety;

namespace FlowLedger.Api.Endpoints;

/// <summary>Data-safety status for the "Data &amp; backups" page: backup history, off-site state, provider API usage.</summary>
internal static class DataEndpoints
{
    internal static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        // Every /api/data endpoint reports on or acts on the WHOLE database (all tenants): household tenant only.
        var group = app.MapGroup("/api/data").WithTags("Data").RequireRateLimiting("api")
            .AddEndpointFilter(HouseholdTenantOnlyAsync);

        group.MapGet("/status", async (
            BackupRunStore store,
            BackupSettings settings,
            NpgsqlProviderCallLedger ledger,
            IJobRunStore jobRuns,
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
            var recentJobRuns = await jobRuns.RecentAsync(20, ct);
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
                // Durable history of scheduled job runs (worker-written, survives restarts), newest first.
                jobRuns = recentJobRuns.Select(r => new
                {
                    r.Job,
                    r.StartedUtc,
                    r.FinishedUtc,
                    durationSeconds = Math.Round((r.FinishedUtc - r.StartedUtc).TotalSeconds, 1),
                    r.Success,
                    r.Error,
                    r.Attempt,
                }).ToList(),
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
        // Anonymous-sized bodies are never buffered: cap the request and the multipart form (default form limit is 128 MB).
        .WithMetadata(
            new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxImportBytes),
            new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxImportBytes })
        .WithName("ImportData")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Import an export zip into an EMPTY database (disaster recovery / moving machines)")
        .RequireRateLimiting("write");

        return app;
    }

    private const string MxProvider = "MX";

    /// <summary>Largest export zip /api/data/import accepts (a household export is a few MB; this leaves ample headroom).</summary>
    internal const long MaxImportBytes = 256L * 1024 * 1024;

    /// <summary>True when the current tenant is the household (operator) tenant that owns the provider credentials.</summary>
    internal static bool IsHouseholdTenant(IServiceProvider services) =>
        services.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>().TenantId
        == FlowLedger.Infrastructure.TenancySettings.Legacy(services.GetRequiredService<IConfiguration>()).LegacyTenantId;

    /// <summary>
    /// Backup state, provider usage, export, import and backup-now are global (they span every tenant), so they are
    /// restricted to the household (legacy) tenant, the same tenant that owns the operator's provider credentials
    /// (<see cref="FlowLedger.Infrastructure.TenancySettings"/>). Any other tenant gets 403 before any work is done.
    /// With OIDC on and no Tenancy:LegacyOwner* configured nobody is mapped to that tenant, so nobody gets in (fail closed).
    /// </summary>
    internal static async ValueTask<object?> HouseholdTenantOnlyAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var services = ctx.HttpContext.RequestServices;
        var household = FlowLedger.Infrastructure.TenancySettings.Legacy(services.GetRequiredService<IConfiguration>()).LegacyTenantId;
        if (services.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>().TenantId != household)
        {
            return Results.Problem(
                title: "Data administration is limited to the household owner",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(ctx);
    }

    /// <summary>
    /// Export, import and backup-now act on the WHOLE database (every tenant). See <see cref="DataAdminGate"/>: an explicit
    /// <c>DataAdmin:Key</c> / <c>X-Admin-Token</c> when configured, otherwise a direct-loopback check that forwarded headers cannot defeat.
    /// </summary>
    private static async ValueTask<object?> LoopbackOnlyAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var verdict = DataAdminGate.Evaluate(http.RequestServices.GetRequiredService<IConfiguration>(), http);
        if (!verdict.Allowed)
        {
            return Results.Problem(title: verdict.Title, detail: verdict.Detail, statusCode: StatusCodes.Status403Forbidden);
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
