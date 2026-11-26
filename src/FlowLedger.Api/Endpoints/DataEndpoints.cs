using FlowLedger.Infrastructure.DataSafety;

namespace FlowLedger.Api.Endpoints;

/// <summary>Data-safety status for the "Data &amp; backups" page: backup history, off-site state, provider API usage.</summary>
internal static class DataEndpoints
{
    internal static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        // Resolved once at registration (not a handler parameter): the backup root is host configuration, never request input.
        var settings = app.ServiceProvider.GetRequiredService<BackupSettings>();
        // Every /api/data endpoint reports on or acts on the WHOLE database (all tenants): household tenant only.
        var group = app.MapGroup("/api/data").WithTags("Data").RequireRateLimiting("api")
            .AddEndpointFilter(HouseholdTenantOnlyAsync);

        group.MapGet("/status", async (
            BackupRunStore store,
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

        group.MapGet("/export", async (
            IConfiguration cfg, FlowLedger.SharedKernel.ITenantContext tenant, string? scope, Guid? tenantId, CancellationToken ct) =>
        {
            // Default stays the whole database (disaster recovery). scope=tenant exports only the caller's tenant; tenantId=<id> exports that tenant (#139).
            if (scope is not null && !scope.Equals("all", StringComparison.OrdinalIgnoreCase) && !scope.Equals("tenant", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(title: "scope must be 'all' or 'tenant'", statusCode: StatusCodes.Status400BadRequest);
            }

            Guid? only = tenantId ?? (string.Equals(scope, "tenant", StringComparison.OrdinalIgnoreCase) ? tenant.TenantId : null);
            var conn = cfg.GetConnectionString("flowledger")!;
            var zip = await SnapshotImport.ExportZipAsync(conn, Path.Combine(settings.BackupRoot, "exports"), ct, only);
            // DeleteOnClose: the unencrypted export zip is removed from the server as soon as it has been sent.
            var stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return Results.File(stream, "application/zip", $"flowledger-export-{(only is null ? "all" : "tenant")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        })
        .WithName("ExportData")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Download data as a portable zip (CSV per table + manifest with hashes); all tenants by default, scope=tenant or tenantId=<id> for one")
        .RequireRateLimiting("write");

        group.MapPost("/import", async (HttpRequest request, IConfiguration cfg, CancellationToken ct) =>
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

        // ── Tenant lifecycle (#169, #189): registry listing, offboarding (erasure) and key rotation ──────────────────────────────
        group.MapGet("/tenants", async (string? status, FlowLedger.Application.Features.Users.ITenantRegistry registry, CancellationToken ct) =>
        {
            if (!Enum.TryParse<FlowLedger.Application.Features.Users.TenantStatus>(status ?? "orphaned", ignoreCase: true, out var parsed))
            {
                return Results.Problem(title: "status must be active, orphaned or deleted", statusCode: StatusCodes.Status400BadRequest);
            }

            var rows = await registry.ListAsync(parsed, ct);
            return Results.Ok(rows.Select(r => new { tenantId = r.Id, status = r.Status.ToString().ToLowerInvariant(), r.CreatedUtc, r.OrphanedUtc, r.DeletedUtc }));
        })
        .WithName("ListTenants")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Tenants in the registry by status (default: orphaned), for offboarding review");

        group.MapPost("/tenants/{tenantId:guid}/offboard", async (
            Guid tenantId,
            OffboardRequest body,
            FlowLedger.Application.Features.Users.ITenantErasure erasure,
            FlowLedger.Application.Features.Users.ITenantRegistry registry,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            // The caller must echo the exact tenant id: a mistyped or replayed route cannot erase the wrong tenant.
            if (body is null || body.ConfirmTenantId != tenantId)
            {
                return Results.Problem(title: "confirmTenantId must equal the tenant id in the route", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await registry.GetAsync(tenantId, ct) is null)
            {
                return Results.Problem(title: "Unknown tenant", statusCode: StatusCodes.Status404NotFound);
            }

            var audit = loggers.CreateLogger("TenantOffboarding");
            try
            {
                var result = await erasure.EraseAsync(tenantId, ct);
                // Audit line: ids and counts only (Guids and integers cannot carry log-forging text).
                audit.LogWarning("Tenant {TenantId} offboarded: {Rows} rows across {Tables} tables, {Files} files erased", tenantId, result.TotalRows, result.RowsDeleted.Count, result.FilesDeleted);
                return Results.Ok(new { tenantId, rowsDeleted = result.RowsDeleted, totalRows = result.TotalRows, filesDeleted = result.FilesDeleted, erasedUtc = result.ErasedUtc });
            }
            catch (FlowLedger.Application.Features.Users.TenantErasureRefusedException ex)
            {
                audit.LogWarning("Tenant {TenantId} offboarding refused", tenantId);
                return Results.Problem(title: "Offboarding refused", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("OffboardTenant")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Permanently erase every row and stored file of one tenant (never the household tenant); audited")
        .RequireRateLimiting("write");

        group.MapPost("/reencrypt-secrets", async (IConfiguration cfg, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var conn = cfg.GetConnectionString("flowledger")!;
            var result = await SecretReencryptor.FromConfiguration(cfg, conn).RunAsync(ct);
            loggers.CreateLogger("SecretRotation").LogWarning("Secrets re-encrypted: {Resealed} resealed, {Current} already current, {Unreadable} unreadable", result.Resealed, result.AlreadyCurrent, result.Unreadable);
            return Results.Ok(new { resealed = result.Resealed, alreadyCurrent = result.AlreadyCurrent, unreadable = result.Unreadable });
        })
        .WithName("ReencryptSecrets")
        .AddEndpointFilter(LoopbackOnlyAsync)
        .WithSummary("Re-encrypt stored secrets under the current key (key rotation, idempotent)")
        .RequireRateLimiting("write");

        return app;
    }

    internal sealed record OffboardRequest(Guid ConfirmTenantId);

    private const string MxProvider = FlowLedger.Integrations.Abstractions.ConnectorIds.Mx;

    /// <summary>Largest export zip /api/data/import accepts (a household export is a few MB; this leaves ample headroom).</summary>
    internal const long MaxImportBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Backup state, provider usage, export, import and backup-now are global (they span every tenant), so they are
    /// restricted to the household (legacy) tenant, the same tenant that owns the operator's provider credentials
    /// (<see cref="FlowLedger.Infrastructure.TenancySettings"/>). Any other tenant gets 403 before any work is done.
    /// With OIDC on and no Tenancy:LegacyOwner* configured nobody is mapped to that tenant, so nobody gets in (fail closed).
    /// </summary>
    internal static async ValueTask<object?> HouseholdTenantOnlyAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (!ctx.HttpContext.RequestServices.GetRequiredService<FlowLedger.Application.Abstractions.IHouseholdTenantAccess>().IsHouseholdTenant)
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
