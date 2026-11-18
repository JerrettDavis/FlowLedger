using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Application.Features.Transactions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal sealed record SuggestionKeyRequest(string Key);

internal static class RecurringFlowEndpoints
{
    internal static IEndpointRouteBuilder MapRecurringFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recurring-flows").WithTags("RecurringFlows")
            .RequireRateLimiting("api");

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
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await handler.HandleAsync(request, ct);
            return Results.Created($"/api/recurring-flows/{result.Id}", result);
        })
        .WithName("CreateRecurringFlow")
        .WithSummary("Create a new recurring flow")
        .RequireRateLimiting("write");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateRecurringFlowRequest request,
            UpdateRecurringFlowHandler handler,
            IValidator<UpdateRecurringFlowRequest> validator,
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
        .WithName("UpdateRecurringFlow")
        .WithSummary("Update amount/model of a recurring flow")
        .RequireRateLimiting("write");

        // Detected recurring income/bills. Keys contain '|', so they travel in the body, not the path.
        group.MapGet("/suggestions", async (RecurringSuggestionService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetSuggestionsAsync(ct)))
            .WithName("ListRecurringSuggestions")
            .WithSummary("Recurring patterns found in transactions that are not yet recurring flows (and were not dismissed)");

        group.MapPost("/suggestions/accept", async ([FromBody] SuggestionKeyRequest request, RecurringSuggestionService svc, CancellationToken ct) =>
        {
            var created = await svc.AcceptAsync(request.Key, ct);
            return created is null ? Results.NotFound() : Results.Created($"/api/recurring-flows/{created.Id}", created);
        })
        .WithName("AcceptRecurringSuggestion")
        .WithSummary("Turn a suggestion into a recurring flow")
        .RequireRateLimiting("write");

        group.MapPost("/suggestions/dismiss", async ([FromBody] SuggestionKeyRequest request, RecurringSuggestionService svc, CancellationToken ct) =>
        {
            await svc.DismissAsync(request.Key, ct);
            return Results.NoContent();
        })
        .WithName("DismissRecurringSuggestion")
        .WithSummary("Never suggest this pattern again")
        .RequireRateLimiting("write");

        group.MapPost("/{id:guid}/flag-remove", async (Guid id, RecurringSuggestionService svc, CancellationToken ct) =>
                await svc.RemoveAndSuppressAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("FlagRecurringFlowNotRecurring")
            .WithSummary("Deactivate a flow and never auto-detect that merchant again")
            .RequireRateLimiting("write");

        group.MapPost("/detect", async (RecurringSuggestionService svc, MerchantAliasService aliases, CancellationToken ct) =>
            {
                await aliases.SeedSheetDefaultsAsync(ct);
                return Results.Ok(await svc.AutoAddAsync(ct));
            })
            .WithName("AutoAddRecurringFlows")
            .WithSummary("Add every high-confidence, fixed-amount pattern as a recurring flow now")
            .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, DeactivateRecurringFlowHandler handler, CancellationToken ct) =>
        {
            var found = await handler.HandleAsync(id, ct);
            return found ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeactivateRecurringFlow")
        .WithSummary("Deactivate a recurring flow")
        .RequireRateLimiting("write");

        return app;
    }
}
