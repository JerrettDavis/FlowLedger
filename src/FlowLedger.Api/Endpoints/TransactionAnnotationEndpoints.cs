using FlowLedger.Application.Features.Transactions;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Notes and category splits on a transaction.</summary>
internal static class TransactionAnnotationEndpoints
{
    internal sealed record SetNoteRequest(string? Note);

    internal sealed record SetSplitsRequest(List<SplitInput>? Splits);

    internal static IEndpointRouteBuilder MapTransactionAnnotationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions").RequireRateLimiting("api");

        group.MapPut("/{id:guid}/note", async (Guid id, [FromBody] SetNoteRequest request, TransactionAnnotationService service, HttpRequest httpRequest, HttpResponse response, GetTransactionHandler reader, CancellationToken ct) =>
            {
                var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
                if (malformed)
                {
                    return RowVersioning.Malformed("transaction");
                }

                await service.SetNoteAsync(id, request.Note, ct, expected);
                await RowVersioning.StampTransactionAsync(response, reader, id, ct);
                return Results.NoContent();
            })
            .WithName("SetTransactionNote")
            .WithSummary("Set a transaction's note (blank removes it)")
            .RequireRateLimiting("write");

        group.MapPut("/{id:guid}/splits", async (Guid id, [FromBody] SetSplitsRequest request, TransactionAnnotationService service, HttpRequest httpRequest, HttpResponse response, GetTransactionHandler reader, CancellationToken ct) =>
            {
                var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
                if (malformed)
                {
                    return RowVersioning.Malformed("transaction");
                }

                await service.SetSplitsAsync(id, request.Splits ?? [], ct, expected);
                await RowVersioning.StampTransactionAsync(response, reader, id, ct);
                return Results.NoContent();
            })
            .WithName("SetTransactionSplits")
            .WithSummary("Replace a transaction's category splits (parts must add up to the amount; empty removes the split)")
            .RequireRateLimiting("write");

        return app;
    }
}
