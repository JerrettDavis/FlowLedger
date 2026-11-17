using FlowLedger.Application.Features.Transactions;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Custom merchant labels, e.g. show "NANONOBLE PTE. LTD. SINGAPORE SGP" as "MiniMax".</summary>
internal static class MerchantAliasEndpoints
{
    internal sealed record SetAliasRequest(string Pattern, string Alias);

    internal static IEndpointRouteBuilder MapMerchantAliasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/merchant-aliases").WithTags("Transactions");

        group.MapGet("/", async (MerchantAliasService service, CancellationToken ct) =>
                Results.Ok(await service.ListAsync(ct)))
            .WithName("ListMerchantAliases")
            .WithSummary("List alias rules (text to match in the bank description -> display name)");

        group.MapPut("/", async ([FromBody] SetAliasRequest request, MerchantAliasService service, CancellationToken ct) =>
            {
                try
                {
                    var changed = await service.SetAsync(request.Pattern, request.Alias, ct);
                    return Results.Ok(new { changed });
                }
                catch (ArgumentException ex)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["alias"] = [ex.Message] });
                }
            })
            .WithName("SetMerchantAlias")
            .WithSummary("Create or replace an alias rule and apply it to existing and future transactions")
            .RequireRateLimiting("write");

        group.MapPost("/seed-sheet-defaults", async (MerchantAliasService service, CancellationToken ct) =>
                Results.Ok(new { added = await service.SeedSheetDefaultsAsync(ct) }))
            .WithName("SeedSheetMerchantAliases")
            .WithSummary("Add alias rules for the services in the household bill sheet (existing rules are kept)")
            .RequireRateLimiting("write");

        group.MapDelete("/", async ([FromQuery] string pattern, MerchantAliasService service, CancellationToken ct) =>
                await service.RemoveAsync(pattern, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteMerchantAlias")
            .WithSummary("Remove an alias rule (already-renamed transactions keep their current name)")
            .RequireRateLimiting("write");

        return app;
    }
}
