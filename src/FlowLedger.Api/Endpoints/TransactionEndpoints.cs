using FlowLedger.Application.Features.Transactions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class TransactionEndpoints
{
    internal static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        group.MapGet("/", async (
            [AsParameters] ListTransactionsQuery query,
            ListTransactionsHandler handler,
            IValidator<ListTransactionsQuery> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(query, ct);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            return Results.Ok(await handler.HandleAsync(query, ct));
        })
        .WithName("ListTransactions")
        .WithSummary("List transactions with optional filtering");

        group.MapGet("/{id:guid}", async (Guid id, GetTransactionHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithName("GetTransaction")
        .WithSummary("Get a single transaction by ID");

        group.MapPost("/", async (
            [FromBody] CreateTransactionRequest request,
            CreateTransactionHandler handler,
            IValidator<CreateTransactionRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var result = await handler.HandleAsync(request, ct);
            return Results.Created($"/api/transactions/{result.Id}", result);
        })
        .WithName("CreateTransaction")
        .WithSummary("Record a manual transaction");

        return app;
    }
}
