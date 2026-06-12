using FlowLedger.Application.Features.Categories;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class CategoryEndpoints
{
    internal static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/categories").WithTags("Categories")
            .RequireRateLimiting("api");

        group.MapGet("/", async (ListCategoriesHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ct)))
            .WithName("ListCategories")
            .WithSummary("List all categories for the current tenant");

        group.MapGet("/{id:guid}", async (Guid id, GetCategoryHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
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

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateCategoryRequest request,
            UpdateCategoryHandler handler,
            IValidator<UpdateCategoryRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await handler.HandleAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithName("UpdateCategory")
        .WithSummary("Rename a category")
        .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, DeleteCategoryHandler handler, CancellationToken ct) =>
        {
            try
            {
                var found = await handler.HandleAsync(id, ct);
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
