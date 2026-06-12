using FlowLedger.Application.Features.RecurringFlows;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class RecurringFlowEndpoints
{
    internal static IEndpointRouteBuilder MapRecurringFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recurring-flows").WithTags("RecurringFlows");

        group.MapGet("/", async (ListRecurringFlowsHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ct)))
            .WithName("ListRecurringFlows")
            .WithSummary("List all active recurring flows for the current tenant");

        group.MapGet("/{id:guid}", async (Guid id, GetRecurringFlowHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithName("GetRecurringFlow")
        .WithSummary("Get a single recurring flow by ID");

        group.MapPost("/", async (
            [FromBody] CreateRecurringFlowRequest request,
            CreateRecurringFlowHandler handler,
            IValidator<CreateRecurringFlowRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var result = await handler.HandleAsync(request, ct);
            return Results.Created($"/api/recurring-flows/{result.Id}", result);
        })
        .WithName("CreateRecurringFlow")
        .WithSummary("Create a new recurring flow");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateRecurringFlowRequest request,
            UpdateRecurringFlowHandler handler,
            IValidator<UpdateRecurringFlowRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var result = await handler.HandleAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithName("UpdateRecurringFlow")
        .WithSummary("Update amount/model of a recurring flow");

        group.MapDelete("/{id:guid}", async (Guid id, DeactivateRecurringFlowHandler handler, CancellationToken ct) =>
        {
            var found = await handler.HandleAsync(id, ct);
            return found ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeactivateRecurringFlow")
        .WithSummary("Deactivate a recurring flow");

        return app;
    }
}
