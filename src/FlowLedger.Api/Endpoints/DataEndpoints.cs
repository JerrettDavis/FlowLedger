using FlowLedger.Api.Auth;
using FlowLedger.Application.Features.DataSafety;
using FlowLedger.Infrastructure.DataSafety;

namespace FlowLedger.Api.Endpoints;

/// <summary>Data-safety status for the "Data &amp; backups" page: backup history, off-site state, provider API usage.</summary>
internal static class DataEndpoints
{
    internal static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        // Every /api/data endpoint reports on or acts on the WHOLE database (all tenants): household tenant only.
        var group = app.MapGroup("/api/data").WithTags("Data").RequireRateLimiting("api")
            .RequireAuthorization(HouseholdPolicy.Name);

        group.MapGet("/status", async (GetDataStatusHandler handler, CancellationToken ct) =>
        {
            var s = await handler.HandleAsync(ct);
            return Results.Ok(new
            {
                environment = s.Environment,
                backup = new
                {
                    healthy = s.BackupHealthy,
                    stale = s.BackupStale,
                    offsiteConfigured = s.OffsiteConfigured,
                    lastSuccessUtc = s.LastSuccessUtc,
                    lastRun = s.LastRun is null ? null : Public(s.LastRun),
                    runs = s.Runs.Select(Public).ToList(),
                },
                // Durable history of scheduled job runs (worker-written, survives restarts), newest first.
                jobRuns = s.JobRuns.Select(r => new
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
                    s.ProviderUsage.Provider,
                    s.ProviderUsage.CallsToday,
                    s.ProviderUsage.CallsThisMonth,
                    dailyBudget = s.ProviderUsage.DailyBudget,
                    monthlyBudget = s.ProviderUsage.MonthlyBudget,
                    paused = s.ProviderUsage.Paused,
                    s.ProviderUsage.ByEndpoint,
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
        .AddEndpointFilter<DataAdminLoopbackFilter>()
        .WithSummary("Run a verified backup (snapshot, restore drill, off-site copy) now")
        .RequireRateLimiting("write");

        group.MapGet("/export", async (ExportDataHandler handler, string? scope, Guid? tenantId, CancellationToken ct) =>
        {
            // Default stays the whole database (disaster recovery). scope=tenant exports only the caller's tenant; tenantId=<id> exports that tenant (#139).
            var outcome = await handler.HandleAsync(scope, tenantId, ct);
            if (outcome.Refused)
            {
                return Results.Problem(title: outcome.Refusal, statusCode: StatusCodes.Status400BadRequest);
            }

            // DeleteOnClose: the unencrypted export zip is removed from the server as soon as it has been sent.
            var stream = new FileStream(outcome.ZipPath!, FileMode.Open, FileAccess.Read, FileShare.Read, ApiLimits.FileStreamBufferBytes, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return Results.File(stream, "application/zip", $"flowledger-export-{(outcome.AllTenants ? "all" : "tenant")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        })
        .WithName("ExportData")
        .AddEndpointFilter<DataAdminLoopbackFilter>()
        .WithSummary("Download data as a portable zip (CSV per table + manifest with hashes); all tenants by default, scope=tenant or tenantId=<id> for one")
        .RequireRateLimiting("write");

        group.MapPost("/import", async (HttpRequest request, ImportDataHandler handler, CancellationToken ct) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count != 1)
            {
                return Results.Problem(title: "Upload one export .zip as multipart form field 'file'.", statusCode: 400);
            }

            await using var upload = request.Form.Files[0].OpenReadStream();
            var result = await handler.HandleAsync(upload, ct);
            return result.Ok ? Results.Ok(result.Report) : Results.Problem(title: "Import refused or failed", detail: string.Join("; ", result.Problems), statusCode: 409);
        })
        .DisableAntiforgery()
        // Anonymous-sized bodies are never buffered: cap the request and the multipart form (default form limit is 128 MB).
        .WithMetadata(
            new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxImportBytes),
            new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxImportBytes })
        .WithName("ImportData")
        .AddEndpointFilter<DataAdminLoopbackFilter>()
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
        .AddEndpointFilter<DataAdminLoopbackFilter>()
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
                ApiLog.TenantOffboarded(audit, tenantId, result.TotalRows, result.RowsDeleted.Count, result.FilesDeleted);
                return Results.Ok(new { tenantId, rowsDeleted = result.RowsDeleted, totalRows = result.TotalRows, filesDeleted = result.FilesDeleted, erasedUtc = result.ErasedUtc });
            }
            catch (FlowLedger.Application.Features.Users.TenantErasureRefusedException ex)
            {
                ApiLog.TenantOffboardingRefused(audit, tenantId);
                return Results.Problem(title: "Offboarding refused", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("OffboardTenant")
        .AddEndpointFilter<DataAdminLoopbackFilter>()
        .WithSummary("Permanently erase every row and stored file of one tenant (never the household tenant); audited")
        .RequireRateLimiting("write");

        group.MapPost("/reencrypt-secrets", async (IConfiguration cfg, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var conn = cfg.GetConnectionString("flowledger")!;
            var result = await SecretReencryptor.FromConfiguration(cfg, conn).RunAsync(ct);
            ApiLog.SecretsReencrypted(loggers.CreateLogger("SecretRotation"), result.Resealed, result.AlreadyCurrent, result.Unreadable);
            return Results.Ok(new { resealed = result.Resealed, alreadyCurrent = result.AlreadyCurrent, unreadable = result.Unreadable });
        })
        .WithName("ReencryptSecrets")
        .AddEndpointFilter<DataAdminLoopbackFilter>()
        .WithSummary("Re-encrypt stored secrets under the current key (key rotation, idempotent)")
        .RequireRateLimiting("write");

        group.MapPost("/raw-archive/seal", async (
            FlowLedger.Infrastructure.Storage.RawArchiveMaintenance archive, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var r = await archive.SealPlaintextAsync(ct);
            ApiLog.RawArchiveSealed(loggers.CreateLogger("RawArchive"), r.Sealed, r.AlreadySealed, r.Skipped, r.Recovered);
            return Results.Ok(new { sealedCount = r.Sealed, alreadySealed = r.AlreadySealed, skipped = r.Skipped, recovered = r.Recovered });
        })
        .WithName("SealRawArchive")
        .AddEndpointFilter<DataAdminLoopbackFilter>()
        .WithSummary("Opt-in: encrypt raw provider payloads written before Storage:EncryptionKey was set (in place, verified, idempotent)")
        .RequireRateLimiting("write");

        return app;
    }

    internal sealed record OffboardRequest(Guid ConfirmTenantId);

    /// <summary>Largest export zip /api/data/import accepts (a household export is a few MB; this leaves ample headroom).</summary>
    internal const long MaxImportBytes = ApiLimits.DataImportBytes;

    /// <summary>Response projection: the snapshot folder name only (never the absolute server path).</summary>
    private static object Public(BackupRunResult r) => Public(new BackupRunView(r.Ok, r.StartedUtc, r.FinishedUtc, r.SnapshotDir, r.Rows, r.OffsiteOk, r.OffsiteDetail, r.DrillOk, r.Error, RestoreDrillSummary.ToView(r.Drill)));

    private static object Public(BackupRunView r) => new
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
        Drill = r.Drill is null ? null : new
        {
            Tables = r.Drill.Tables.Select(t => new { t.Table, t.ExpectedRows, t.RestoredRows, t.LiveRows, t.Ok }).ToList(),
            r.Drill.RawArchiveChecked,
            r.Drill.RawObjectsVerified,
            r.Drill.RawObjectsSealed,
            r.Drill.Problems,
        },
    };
}
