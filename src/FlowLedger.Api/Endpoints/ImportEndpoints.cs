using FlowLedger.Application.Features.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// CSV import and planned-vs-actual reconciliation endpoints.
///
/// POST /api/imports        — import CSV transactions for an account
/// GET  /api/imports/suggestions — list NeedsReview match candidates
/// POST /api/transactions/{id}/match   — manually match to a planned occurrence
/// POST /api/transactions/{id}/unmatch — remove a match link
/// </summary>
internal static class ImportEndpoints
{
    private static async Task AfterImportAsync(
        ImportSummaryDto summary,
        FlowLedger.Application.Abstractions.ISheetAutoMarker sheetMarker,
        FlowLedger.Application.Features.Duplicates.DuplicateService duplicates,
        FlowLedger.Application.Features.Integrations.IntegrationService integrations,
        ILogger logger,
        CancellationToken ct)
    {
        if (summary.ImportedCount <= 0)
        {
            return;
        }

        await integrations.DispatchAsync(
            FlowLedger.Application.Features.Integrations.IntegrationEvents.ImportCompleted,
            new { imported = summary.ImportedCount },
            ct);

        // A file often overlaps what the bank already synced: fold the unambiguous overlaps now.
        try
        {
            await duplicates.AutoMergeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Duplicate merge after import failed.");
        }

        try
        {
            await sheetMarker.RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Bill sheet refresh after import failed.");
        }
    }

    internal static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Import ────────────────────────────────────────────────────────────

        app.MapPost("/api/imports", async (
            [FromBody] ImportRequest request,
            ImportTransactionsHandler handler,
            FlowLedger.Application.Abstractions.ISheetAutoMarker sheetMarker,
            FlowLedger.Application.Features.Duplicates.DuplicateService duplicates,
            FlowLedger.Application.Features.Integrations.IntegrationService integrations,
            ILogger<ImportTransactionsHandler> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.CsvContent))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.CsvContent)] = ["CSV content must not be empty."]
                });
            }

            if (request.AccountId == Guid.Empty)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.AccountId)] = ["AccountId is required."]
                });
            }

            try
            {
                var mapping = new CsvColumnMapping(
                    DateColumnIndex: request.DateColumnIndex,
                    AmountColumnIndex: request.AmountColumnIndex,
                    DescriptionColumnIndex: request.DescriptionColumnIndex,
                    MerchantColumnIndex: request.MerchantColumnIndex,
                    CategoryColumnIndex: request.CategoryColumnIndex,
                    Delimiter: string.IsNullOrEmpty(request.Delimiter) ? ',' : request.Delimiter[0],
                    DateFormats: request.DateFormats,
                    HasHeaderRow: request.HasHeaderRow);

                var command = new ImportTransactionsCommand(request.AccountId, request.CsvContent, mapping);
                var summary = await handler.HandleAsync(command, ct);
                await AfterImportAsync(summary, sheetMarker, duplicates, integrations, logger, ct);

                return Results.Ok(summary);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Import failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(5 * 1024 * 1024))
        .WithName("ImportTransactions")
        .WithTags("Imports")
        .WithSummary("Import transactions from CSV")
        .RequireRateLimiting("write");

        app.MapGet("/api/imports/formats", () => Results.Ok(ImportNormalizer.Formats))
            .WithName("ListImportFormats")
            .WithTags("Imports")
            .WithSummary("Supported import sources (apps, banks and file types)")
            .RequireRateLimiting("api");

        app.MapPost("/api/imports/auto", async (
            [FromBody] AutoImportRequest request,
            ImportTransactionsHandler handler,
            FlowLedger.Application.Abstractions.ISheetAutoMarker sheetMarker,
            FlowLedger.Application.Features.Duplicates.DuplicateService duplicates,
            FlowLedger.Application.Features.Integrations.IntegrationService integrations,
            ILogger<ImportTransactionsHandler> logger,
            CancellationToken ct) =>
        {
            if (request.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["import"] = ["An account and file content are required."] });
            }

            try
            {
                var normalized = ImportNormalizer.Normalize(request.Content, request.FormatId, new ImportHints(request.DateOrder, request.NumberFormat));
                var summary = await handler.HandleAsync(
                    new ImportTransactionsCommand(request.AccountId, normalized.Csv, NormalizedImport.Mapping), ct);
                await AfterImportAsync(summary, sheetMarker, duplicates, integrations, logger, ct);
                return Results.Ok(new AutoImportResult(normalized.FormatId, normalized.FormatName, normalized.Warnings, summary));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: "Import failed", detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(6 * 1024 * 1024))
        .WithName("ImportTransactionsAuto")
        .WithTags("Imports")
        .WithSummary("Import a Mint, Monarch, YNAB, bank CSV, OFX/QFX or QIF file with automatic format detection")
        .RequireRateLimiting("write");

        // ── Match suggestions ────────────────────────────────────────────────

        app.MapGet("/api/imports/suggestions", async (
            ListMatchSuggestionsHandler handler,
            CancellationToken ct) =>
        {
            var suggestions = await handler.HandleAsync(ct);
            return Results.Ok(suggestions);
        })
        .WithName("ListMatchSuggestions")
        .WithTags("Imports")
        .WithSummary("List transactions needing reconciliation review")
        .RequireRateLimiting("api");

        // ── Manual match ─────────────────────────────────────────────────────

        app.MapPost("/api/transactions/{id:guid}/match", async (
            Guid id,
            [FromBody] ManualMatchRequest request,
            MatchTransactionHandler handler,
            CancellationToken ct) =>
        {
            if (request.PlannedOccurrenceId == Guid.Empty)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.PlannedOccurrenceId)] = ["PlannedOccurrenceId is required."]
                });
            }

            try
            {
                var result = await handler.HandleAsync(
                    new MatchTransactionCommand(id, request.PlannedOccurrenceId), ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Match failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        })
        .WithName("MatchTransaction")
        .WithTags("Imports")
        .WithSummary("Manually match a transaction to a planned occurrence")
        .RequireRateLimiting("write");

        // ── Unmatch ──────────────────────────────────────────────────────────

        app.MapPost("/api/transactions/{id:guid}/unmatch", async (
            Guid id,
            UnmatchTransactionHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var result = await handler.HandleAsync(new UnmatchTransactionCommand(id), ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Unmatch failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        })
        .WithName("UnmatchTransaction")
        .WithTags("Imports")
        .WithSummary("Remove a match link from a transaction")
        .RequireRateLimiting("write");

        return app;
    }
}

// ── Request models ────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/imports.</summary>
public sealed record ImportRequest(
    Guid AccountId,
    string CsvContent,
    int DateColumnIndex = 0,
    int AmountColumnIndex = 1,
    int DescriptionColumnIndex = 2,
    int? MerchantColumnIndex = null,
    int? CategoryColumnIndex = null,
    string? Delimiter = null,
    string[]? DateFormats = null,
    bool HasHeaderRow = true);

/// <summary>Request body for POST /api/transactions/{id}/match.</summary>
public sealed record ManualMatchRequest(Guid PlannedOccurrenceId);

/// <summary>Request body for POST /api/imports/auto. <paramref name="FormatId"/> overrides detection.</summary>
public sealed record AutoImportRequest(Guid AccountId, string Content, string? FormatId = null, string? DateOrder = null, string? NumberFormat = null);

/// <summary>What was detected plus the import summary.</summary>
public sealed record AutoImportResult(string FormatId, string FormatName, IReadOnlyList<string> Warnings, ImportSummaryDto Summary);
