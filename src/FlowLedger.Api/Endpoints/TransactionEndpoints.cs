using FlowLedger.Application.Features.Transactions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class TransactionEndpoints
{
    internal static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions")
            .RequireRateLimiting("api");

        group.MapGet("/", async (
            [AsParameters] ListTransactionsQuery query,
            ListTransactionsHandler handler,
            IValidator<ListTransactionsQuery> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(query, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            return Results.Ok(await handler.HandleAsync(query, ct));
        })
        .WithName("ListTransactions")
        .WithSummary("List transactions with optional filtering");

        group.MapGet("/export.csv", async (
            [AsParameters] ListTransactionsQuery query,
            ListTransactionsHandler handler,
            CancellationToken ct) =>
        {
            const int Page = 500, Cap = 50_000;
            var all = new List<TransactionDto>();
            for (var skip = 0; all.Count < Cap; skip += Page)
            {
                var batch = await handler.HandleAsync(query with { Skip = skip, Take = Page }, ct);
                all.AddRange(batch);
                if (batch.Count < Page)
                {
                    break;
                }
            }

            return Results.File(System.Text.Encoding.UTF8.GetBytes(TransactionCsv.Build(all)), "text/csv; charset=utf-8",
                $"flowledger-transactions-{DateTime.UtcNow:yyyyMMdd}.csv");
        })
        .WithName("ExportTransactionsCsv")
        .WithSummary("Download transactions (filtered like the list) as CSV");

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
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            try
            {
                var result = await handler.HandleAsync(request, ct);
                return Results.Created($"/api/transactions/{result.Id}", result);
            }
            catch (TransactionQuotaExceededException ex)
            {
                return Results.Problem(title: "Transaction limit reached", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("CreateTransaction")
        .WithSummary("Record a manual transaction")
        .RequireRateLimiting("write");

        return app;
    }
}
