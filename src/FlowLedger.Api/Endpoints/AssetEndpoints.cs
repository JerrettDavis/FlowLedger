using FlowLedger.Application.Features.Assets;
using FlowLedger.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class AssetEndpoints
{
    internal static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets").WithTags("Assets").RequireRateLimiting("api");

        group.MapGet("/", async (ListAssetsHandler h, CancellationToken ct) => Results.Ok(await h.HandleAsync(ct)))
            .WithName("ListAssets").WithSummary("List active assets with estimated values");

        group.MapGet("/summary", async (GetAssetSummaryHandler h, CancellationToken ct) => Results.Ok(await h.HandleAsync(ct)))
            .WithName("GetAssetSummary").WithSummary("Asset totals, gain/loss, equity, category breakdown and net worth");

        group.MapGet("/categories", () => Results.Ok(AssetCategoryTemplates.List()))
            .WithName("ListAssetCategories").WithSummary("Categories with suggested fields and valuation defaults");

        group.MapGet("/{id:guid}", async (Guid id, GetAssetHandler h, CancellationToken ct) =>
        {
            var result = await h.HandleAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("GetAsset").WithSummary("Get an asset with its valuation history");

        group.MapPost("/", (
            [FromBody] CreateAssetRequest request,
            CreateAssetHandler h,
            IValidator<CreateAssetRequest> validator,
            CancellationToken ct) => Guarded(validator, request, async () =>
        {
            var result = await h.HandleAsync(request, ct);
            return Results.Created($"/api/assets/{result.Id}", result);
        }, ct))
            .WithName("CreateAsset").WithSummary("Add an asset").RequireRateLimiting("write");

        group.MapPut("/{id:guid}", (
            Guid id,
            [FromBody] UpdateAssetRequest request,
            UpdateAssetHandler h,
            IValidator<UpdateAssetRequest> validator,
            CancellationToken ct) => Guarded(validator, request, async () =>
        {
            var result = await h.HandleAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }, ct))
            .WithName("UpdateAsset").WithSummary("Update asset details, valuation model and account link").RequireRateLimiting("write");

        group.MapPost("/{id:guid}/valuations", (
            Guid id,
            [FromBody] RecordValuationRequest request,
            RecordAssetValuationHandler h,
            IValidator<RecordValuationRequest> validator,
            CancellationToken ct) => Guarded(validator, request, async () =>
        {
            var result = await h.HandleAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }, ct))
            .WithName("RecordAssetValuation").WithSummary("Record a new valuation (appraisal, estimate…)").RequireRateLimiting("write");

        group.MapPost("/{id:guid}/refresh", async (
            Guid id,
            [FromQuery] string? feed,
            RefreshAssetValuationHandler h,
            CancellationToken ct) =>
        {
            try
            {
                var result = await h.HandleAsync(id, feed, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex) when (ex is AssetOperationException or DomainException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithName("RefreshAssetValuation").WithSummary("Fetch a fresh valuation from a feed (default: model)")
            .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, DeactivateAssetHandler h, CancellationToken ct) =>
                await h.HandleAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeactivateAsset").WithSummary("Remove (deactivate) an asset").RequireRateLimiting("write");

        return app;
    }

    private static async Task<IResult> Guarded<T>(IValidator<T> validator, T request, Func<Task<IResult>> action, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.ToDictionary());
        }

        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is AssetOperationException or DomainException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
