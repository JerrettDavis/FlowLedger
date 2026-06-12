using FlowLedger.Application.Features.Imports;
using Microsoft.AspNetCore.Http;
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
            ImportTransactionsHandler handler,
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
        .WithName("ImportTransactions")
        .WithTags("Imports")
        .WithSummary("Import transactions from CSV");

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
        .WithSummary("List transactions needing reconciliation review");

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
        .WithSummary("Manually match a transaction to a planned occurrence");

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
        .WithSummary("Remove a match link from a transaction");

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
