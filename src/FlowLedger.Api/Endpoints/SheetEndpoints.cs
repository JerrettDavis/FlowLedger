using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.BillSheet;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class SheetEndpoints
{
    internal static IEndpointRouteBuilder MapSheetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sheet").WithTags("Sheet").RequireRateLimiting("api");

        group.MapGet("/", async (string? month, int? months, BillSheetService s, CancellationToken ct)
            => Results.Ok(await s.GetViewAsync(month, months, ct)))
            .WithName("GetBillSheet").WithSummary("The spreadsheet-mode grid: rows, month cells, per-section totals");

        group.MapPost("/refresh", (ISheetAutoMarker marker, CancellationToken ct) => Guarded(async () =>
            Results.Ok(await marker.RefreshAsync(ct))))
            .WithName("RefreshBillSheet").WithSummary("Mark cells paid from matching transactions").RequireRateLimiting("write");

        group.MapPost("/bills", ([FromBody] SaveBillRequest r, BillSheetService s, CancellationToken ct) => Guarded(async () =>
        {
            var id = await s.CreateBillAsync(r, ct);
            return Results.Created($"/api/sheet/bills/{id}", new { id });
        })).WithName("CreateSheetBill").WithSummary("Add a row").RequireRateLimiting("write");

        group.MapPut("/bills/{id:guid}", (Guid id, [FromBody] SaveBillRequest r, BillSheetService s, CancellationToken ct) => Guarded(async () =>
            await s.UpdateBillAsync(id, r, ct) ? Results.NoContent() : Results.NotFound()))
            .WithName("UpdateSheetBill").WithSummary("Edit a row").RequireRateLimiting("write");

        group.MapDelete("/bills/{id:guid}", async (Guid id, BillSheetService s, CancellationToken ct)
            => await s.DeleteBillAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteSheetBill").WithSummary("Delete a row and its cells").RequireRateLimiting("write");

        group.MapPut("/bills/{id:guid}/payments/{month}", (Guid id, string month, [FromBody] SetPaymentRequest r, BillSheetService s, CancellationToken ct) => Guarded(async () =>
            await s.SetPaymentAsync(id, month, r, ct) ? Results.NoContent() : Results.NotFound()))
            .WithName("SetSheetPayment").WithSummary("Manually mark a cell paid or unpaid").RequireRateLimiting("write");

        group.MapDelete("/bills/{id:guid}/payments/{month}", (Guid id, string month, int? slot, BillSheetService s, CancellationToken ct) => Guarded(async () =>
            await s.ClearPaymentAsync(id, month, slot, ct) ? Results.NoContent() : Results.NotFound()))
            .WithName("ClearSheetPayment").WithSummary("Return a cell to automatic").RequireRateLimiting("write");

        group.MapPost("/import", ([FromBody] ImportSheetRequest r, BillSheetService s, CancellationToken ct) => Guarded(async () =>
            Results.Ok(await s.ImportCsvAsync(r, ct))))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(5 * 1024 * 1024))
            .WithName("ImportBillSheet").WithSummary("Load rows and paid marks from a CSV export of the workbook").RequireRateLimiting("write");

        return app;
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SheetOperationException ex)
        {
            return Results.Problem(title: "Invalid sheet request", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
