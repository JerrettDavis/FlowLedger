using FlowLedger.Application.Features.Duplicates;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class DuplicateEndpoints
{
    internal sealed record PairRequest(Guid FirstId, Guid SecondId, Guid? KeepId = null);

    internal static IEndpointRouteBuilder MapDuplicateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/duplicates").WithTags("Duplicates");

        group.MapGet("/", async (DuplicateService service, CancellationToken ct) => Results.Ok(await service.ScanAsync(ct)))
            .WithName("ScanDuplicates")
            .WithSummary("Suspected duplicate transactions: High (safe to merge automatically) and Possible (needs a human). Read-only.");

        group.MapPost("/auto-merge", async (DuplicateService service, CancellationToken ct) => Results.Ok(await service.AutoMergeAsync(ct)))
            .WithName("AutoMergeDuplicates")
            .WithSummary("Merge every High-confidence pair now (also runs after each sync and CSV import). Possible pairs are never touched.")
            .RequireRateLimiting("write");

        group.MapPost("/merge", async ([FromBody] PairRequest request, DuplicateService service, CancellationToken ct) =>
            {
                return await service.MergeAsync(request.FirstId, request.SecondId, request.KeepId, ct)
                    ? Results.Ok(new { merged = true })
                    : Results.NotFound();
            })
            .WithName("MergeDuplicate")
            .WithSummary("Merge two transactions you confirmed are the same. The removed row is kept in the merge log and cannot be re-synced.")
            .RequireRateLimiting("write");

        group.MapPost("/dismiss", async ([FromBody] PairRequest request, DuplicateService service, CancellationToken ct) =>
            {
                await service.DismissAsync(request.FirstId, request.SecondId, ct);
                return Results.NoContent();
            })
            .WithName("DismissDuplicate")
            .WithSummary("Mark a suggested pair as two genuinely separate transactions; it will not be suggested again.")
            .RequireRateLimiting("write");

        group.MapGet("/log", async (int? take, DuplicateService service, CancellationToken ct) =>
                Results.Ok(await service.RecentMergesAsync(take ?? 50, ct)))
            .WithName("DuplicateMergeLog")
            .WithSummary("Recent merges (automatic and manual) with a JSON snapshot of each removed row.");

        return app;
    }
}
