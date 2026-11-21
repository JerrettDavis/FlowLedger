using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.LocalAi;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// In-process AI helpers (embedding similarity, merchant grouping, held-out evaluation, model install). Everything runs inside
/// this process; the only network use is the optional, explicit, hash-verified model download from the pinned catalog.
/// </summary>
internal static class LocalAiEndpoints
{
    internal static IEndpointRouteBuilder MapLocalAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/local-ai/status", (LocalAiService service, HttpContext http) =>
            {
                var status = service.Status();
                // Installation-level detail (server paths, exception text) is for the household owner only.
                return Results.Ok(DataEndpoints.IsHouseholdTenant(http.RequestServices)
                    ? status
                    : status with
                    {
                        ModelDirectory = string.Empty,
                        EmbedderNote = status.EmbedderNote is null ? null : "See the household owner.",
                        LlmNote = status.LlmNote is null ? null : "See the household owner.",
                        Download = status.Download is null ? null : status.Download with { Error = status.Download.Error is null ? null : "Failed." },
                    });
            })
            .WithTags("Local AI").WithName("LocalAiStatus")
            .WithSummary("Which in-process embedder is really active, which models are installed, and any download progress");

        app.MapPost("/api/local-ai/models/{id}/download", (string id, ILocalAiModels models) =>
                models.TryStartDownload(id, out var error)
                    ? Results.Accepted()
                    : Results.ValidationProblem(new Dictionary<string, string[]> { ["id"] = [error ?? "Cannot start download."] }))
            .WithTags("Local AI").WithName("LocalAiDownloadModel")
            .WithSummary("Download a catalog model (pinned revision, SHA-256 verified). Takes a catalog id, never a URL. Household owner only: the model directory is shared by the whole installation.")
            .AddEndpointFilter(DataEndpoints.HouseholdTenantOnlyAsync)
            .RequireRateLimiting("write");

        app.MapDelete("/api/local-ai/models/{id}", (string id, ILocalAiModels models) =>
                models.Remove(id) ? Results.NoContent() : Results.NotFound())
            .WithTags("Local AI").WithName("LocalAiRemoveModel")
            .WithSummary("Delete an installed model's files (household owner only)")
            .AddEndpointFilter(DataEndpoints.HouseholdTenantOnlyAsync)
            .RequireRateLimiting("write");

        app.MapGet("/api/local-ai/similar/{transactionId:guid}", async (
                Guid transactionId, int? top, LocalAiService service, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await service.SimilarAsync(transactionId, top ?? 10, ct));
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound();
                }
            })
            .WithTags("Local AI").WithName("LocalAiSimilarTransactions")
            .WithSummary("Transactions whose merchant text is most similar to this one")
            .RequireRateLimiting("write");

        app.MapGet("/api/local-ai/merchant-groups", async (double? threshold, LocalAiService service, CancellationToken ct) =>
                Results.Ok(await service.MerchantGroupsAsync(threshold, ct)))
            .WithTags("Local AI").WithName("LocalAiMerchantGroups")
            .WithSummary("Merchant names that likely name the same business (suggestions only; accept one to create aliases)")
            .RequireRateLimiting("write");

        app.MapPost("/api/local-ai/evaluate", async (int? folds, LocalAiService service, CancellationToken ct) =>
                Results.Ok(await service.EvaluateAsync(folds ?? 5, ct)))
            .WithTags("Local AI").WithName("LocalAiEvaluate")
            .WithSummary("Held-out-merchant cross-validation of the categorizers on this household's own manual categories")
            .RequireRateLimiting("write");

        return app;
    }
}
