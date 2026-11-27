using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class CategorizationEndpoints
{
    internal sealed record SetCategoryRequest(Guid CategoryId, bool ApplyToMerchant = false);

    internal sealed record BulkCategoryRequest(List<Guid> TransactionIds, Guid CategoryId);

    internal sealed record RestoreCategoriesRequest(Guid AppliedCategoryId, List<CategoryRestore>? Items);

    internal sealed record RecategorizeRequest(Guid CategoryId, bool ApplyToAll = false, string? Note = null);

    internal static IEndpointRouteBuilder MapCategorizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/transactions/{id:guid}/recategorize", async (
                Guid id, [FromBody] RecategorizeRequest request, CategoryRuleService service, HttpRequest httpRequest, HttpResponse response, GetTransactionHandler reader, CancellationToken ct) =>
            {
                var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
                if (malformed)
                {
                    return RowVersioning.Malformed("transaction");
                }

                var recategorized = await service.RecategorizeAsync(id, request.CategoryId, request.ApplyToAll, ct, request.Note, expected);
                await RowVersioning.StampTransactionAsync(response, reader, id, ct);
                return Results.Ok(new { changed = recategorized });
            })
            .WithTags("Transactions").WithName("RecategorizeTransaction")
            .WithSummary("Set a transaction's category by hand; optionally re-label every matching merchant and remember a rule for future syncs")
            .RequireRateLimiting("write");

        app.MapPost("/api/categorization/run", async (bool? llm, CategorizationService service, CancellationToken ct) =>
                Results.Ok(await service.RunAsync(llm == true, ct)))
            .WithTags("Categories").WithName("RunCategorization")
            .WithSummary("Fill in missing categories (transfers, learned merchants, keyword rules, models). Never overwrites existing categories. ?llm=true lets the optional in-process language model suggest for rows the embedder is unsure about (slow; capped per run).")
            .RequireRateLimiting("write");

        app.MapPost("/api/transactions/enrich", async (TransactionEnrichmentService service, CancellationToken ct) =>
                Results.Ok(await service.RunAsync(ct)))
            .WithTags("Transactions").WithName("EnrichTransactions")
            .WithSummary("Re-run automatic tagging and merchant-name suggestions over all transactions (user tags and aliases are never touched)")
            .RequireRateLimiting("write");

        app.MapPost("/api/categorization/clear-auto", async (CategorizationService service, CancellationToken ct) =>
                Results.Ok(new { cleared = await service.ClearAutoCategoriesAsync(ct) }))
            .WithTags("Categories").WithName("ClearAutoCategories")
            .WithSummary("Remove categories that were set automatically (manual ones are never touched)")
            .RequireRateLimiting("write");

        app.MapPost("/api/transactions/bulk-category", async ([FromBody] BulkCategoryRequest request, CategorizationService service, CancellationToken ct) =>
                Results.Ok(new { changed = await service.SetCategoryBulkAsync(request.TransactionIds ?? [], request.CategoryId, ct) }))
            .WithTags("Transactions").WithName("BulkSetTransactionCategory")
            .WithSummary("Set one category (manual) on up to 500 selected transactions")
            .RequireRateLimiting("write");

        app.MapGet("/api/transactions/{id:guid}/same-merchant-count", async (Guid id, CategorizationService service, CancellationToken ct) =>
                Results.Ok(new { count = await service.CountSameMerchantAsync(id, ct) }))
            .WithTags("Transactions").WithName("CountSameMerchantTransactions")
            .WithSummary("How many other transactions 'apply to same merchant' would re-label (uncategorized or auto-categorized, same merchant)")
            .RequireRateLimiting("api");

        app.MapPost("/api/transactions/restore-categories", async ([FromBody] RestoreCategoriesRequest request, CategorizationService service, CancellationToken ct) =>
                Results.Ok(new { restored = await service.RestoreCategoriesAsync(request.AppliedCategoryId, request.Items ?? [], ct) }))
            .WithTags("Transactions").WithName("RestoreTransactionCategories")
            .WithSummary("Undo a category change: put rows back to their previous category while they still carry the applied one")
            .RequireRateLimiting("write");

        app.MapPut("/api/transactions/{id:guid}/category", async (
                Guid id, [FromBody] SetCategoryRequest request, CategorizationService service, HttpRequest httpRequest, HttpResponse response, GetTransactionHandler reader, CancellationToken ct) =>
            {
                var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
                if (malformed)
                {
                    return RowVersioning.Malformed("transaction");
                }

                var result = await service.SetCategoryWithUndoAsync(id, request.CategoryId, request.ApplyToMerchant, ct, expected);
                await RowVersioning.StampTransactionAsync(response, reader, id, ct);
                return Results.Ok(new { changed = result.Changed, restore = result.Restore });
            })
            .WithTags("Transactions").WithName("SetTransactionCategory")
            .WithSummary("Set a transaction's category (manual); optionally apply to all uncategorized transactions of the same merchant")
            .RequireRateLimiting("write");

        return app;
    }
}
