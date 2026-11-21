using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class CategorizationEndpoints
{
    internal sealed record SetCategoryRequest(Guid CategoryId, bool ApplyToMerchant = false);

    internal sealed record RecategorizeRequest(Guid CategoryId, bool ApplyToAll = false, string? Note = null);

    internal static IEndpointRouteBuilder MapCategorizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/transactions/{id:guid}/recategorize", async (
                Guid id, [FromBody] RecategorizeRequest request, CategoryRuleService service, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(new { changed = await service.RecategorizeAsync(id, request.CategoryId, request.ApplyToAll, ct, request.Note) });
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound();
                }
                catch (ArgumentException ex)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = [ex.Message] });
                }
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

        app.MapPut("/api/transactions/{id:guid}/category", async (
                Guid id, [FromBody] SetCategoryRequest request, CategorizationService service, CancellationToken ct) =>
            {
                try
                {
                    var changed = await service.SetCategoryAsync(id, request.CategoryId, request.ApplyToMerchant, ct);
                    return Results.Ok(new { changed });
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound();
                }
            })
            .WithTags("Transactions").WithName("SetTransactionCategory")
            .WithSummary("Set a transaction's category (manual); optionally apply to all uncategorized transactions of the same merchant")
            .RequireRateLimiting("write");

        return app;
    }
}
