using FlowLedger.Application.Features.Categories;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class CategorizationEndpoints
{
    internal sealed record SetCategoryRequest(Guid CategoryId, bool ApplyToMerchant = false);

    internal static IEndpointRouteBuilder MapCategorizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/categorization/run", async (CategorizationService service, CancellationToken ct) =>
                Results.Ok(await service.RunAsync(ct)))
            .WithTags("Categories").WithName("RunCategorization")
            .WithSummary("Fill in missing categories (transfers, learned merchants, keyword rules). Never overwrites existing categories.")
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
