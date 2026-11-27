using FlowLedger.Application.Features.Categories;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class CategoryEndpoints
{
    internal sealed record MoveCategoryRequest(string Path, string NewPath, bool DryRun = false);

    internal static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/categories").WithTags("Categories")
            .RequireRateLimiting("api");

        group.MapGet("/", async (ListCategoriesHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ct)))
            .WithName("ListCategories")
            .WithSummary("List all categories for the current tenant");

        group.MapPost("/defaults", async (RestoreDefaultCategoriesHandler handler, CancellationToken ct) =>
            Results.Ok(new { added = await handler.HandleAsync(ct) }))
            .WithName("RestoreDefaultCategories")
            .WithSummary("Add any missing default (Mint-equivalent) categories; existing categories are untouched");

        group.MapGet("/{id:guid}", async (Guid id, GetCategoryHandler handler, HttpResponse response, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(id, ct);
            return result is null ? Results.NotFound() : RowVersioning.Ok(response, result, result.Version);
        })
        .WithName("GetCategory")
        .WithSummary("Get a single category by ID");

        group.MapPost("/", async (
            [FromBody] CreateCategoryRequest request,
            CreateCategoryHandler handler,
            IValidator<CreateCategoryRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await handler.HandleAsync(request, ct);
            return Results.Created($"/api/categories/{result.Id}", result);
        })
        .WithName("CreateCategory")
        .WithSummary("Create a new category")
        .RequireRateLimiting("write");

        group.MapPost("/move", async (
            [FromBody] MoveCategoryRequest request,
            CategoryRestructureService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.MoveAsync(request.Path, request.NewPath, request.DryRun, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = [ex.Message] });
            }
        })
        .WithName("MoveCategory")
        .WithSummary("Rename/move a category and its subtree to a new path; where the path already exists the categories are merged. dryRun previews.")
        .RequireRateLimiting("write");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateCategoryRequest request,
            UpdateCategoryHandler handler,
            IValidator<UpdateCategoryRequest> validator,
            HttpRequest httpRequest,
            HttpResponse response,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
            if (malformed)
            {
                return RowVersioning.Malformed("category");
            }

            var result = await handler.HandleAsync(id, request, ct, expected);
            return result is null ? Results.NotFound() : RowVersioning.Ok(response, result, result.Version);
        })
        .WithName("UpdateCategory")
        .WithSummary("Rename a category")
        .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, DeleteCategoryHandler handler, HttpRequest httpRequest, CancellationToken ct) =>
        {
            var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
            if (malformed)
            {
                return RowVersioning.Malformed("category");
            }

            try
            {
                var found = await handler.HandleAsync(id, ct, expected);
                return found ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        })
        .WithName("DeleteCategory")
        .WithSummary("Delete a non-system category")
        .RequireRateLimiting("write");

        return app;
    }
}
