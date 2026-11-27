using FlowLedger.Application.Features.Imports;
using Microsoft.AspNetCore.Mvc;

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
    internal static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Import ────────────────────────────────────────────────────────────

        app.MapPost("/api/imports", async (
            [FromBody] ImportRequest request,
            ImportCoordinator coordinator,
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
            return Results.Ok(await coordinator.ImportAsync(command, ct));
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ApiLimits.CsvImportBytes))
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
            ImportCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (request.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["import"] = ["An account and file content are required."] });
            }

            return Results.Ok(await coordinator.ImportAutoAsync(
                request.AccountId, request.Content, request.FormatId, new ImportHints(request.DateOrder, request.NumberFormat), ct));
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ApiLimits.ImportRequestBytes))
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

            var result = await handler.HandleAsync(
                new MatchTransactionCommand(id, request.PlannedOccurrenceId), ct);
            return Results.Ok(result);
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
            var result = await handler.HandleAsync(new UnmatchTransactionCommand(id), ct);
            return Results.Ok(result);
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
