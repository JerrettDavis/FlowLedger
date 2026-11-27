using FlowLedger.Application.Features.Categories;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>User-defined categorization rules: description contains X -> category Y.</summary>
internal static class CategoryRuleEndpoints
{
    internal sealed record SetRuleRequest(string Pattern, string CategoryPath);

    internal static IEndpointRouteBuilder MapCategoryRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/category-rules").WithTags("Categories").RequireRateLimiting("api");

        group.MapGet("/", async (CategoryRuleService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .WithName("ListCategoryRules")
            .WithSummary("List categorization rules");

        group.MapPut("/", async ([FromBody] SetRuleRequest request, CategoryRuleService service, CancellationToken ct) =>
                Results.Ok(new { changed = await service.SetAsync(request.Pattern, request.CategoryPath, ct) }))
            .WithName("SetCategoryRule")
            .WithSummary("Create or replace a rule and apply it to existing transactions")
            .RequireRateLimiting("write");

        group.MapDelete("/", async ([FromQuery] string pattern, CategoryRuleService service, CancellationToken ct) =>
                await service.RemoveAsync(pattern, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteCategoryRule")
            .WithSummary("Remove a rule (already-categorized transactions keep their category)")
            .RequireRateLimiting("write");

        return app;
    }
}
