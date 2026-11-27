using FlowLedger.Application.Features.Query;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class QueryEndpoints
{
    internal static IEndpointRouteBuilder MapQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var query = app.MapGroup("/api/query").WithTags("Query").RequireRateLimiting("api");

        query.MapGet("/schema", (GetQuerySchemaHandler h) => Results.Ok(h.Handle()))
            .WithName("GetQuerySchema").WithSummary("Datasets, columns, functions and operators available to queries");

        query.MapPost("/run", async ([FromBody] RunQueryRequest request, RunQueryHandler h, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await h.HandleAsync(request.Query ?? "", ct));
            }
            catch (QueryException ex)
            {
                return Results.BadRequest(new { error = ex.Message, position = ex.Position });
            }
        }).WithName("RunQuery").WithSummary("Run a pipeline query against your data");

        query.MapPost("/ask", async ([FromBody] AskQueryRequest request, AskQueryHandler h, CancellationToken ct) =>
                Results.Ok(await h.HandleAsync(request.Question ?? "", ct)))
            .WithName("AskQuery")
            .WithSummary("Turn a plain-English question into a validated query using a local model (nothing leaves the machine)")
            .RequireRateLimiting("write");

        var reports = app.MapGroup("/api/reports").WithTags("Reports").RequireRateLimiting("api");

        reports.MapGet("/", async (ListReportsHandler h, CancellationToken ct) => Results.Ok(await h.HandleAsync(ct)))
            .WithName("ListReports").WithSummary("Saved and preset views and reports");

        reports.MapGet("/{id}", async (string id, GetReportHandler h, CancellationToken ct) =>
        {
            var r = await h.HandleAsync(id, ct);
            return r is null ? Results.NotFound() : Results.Ok(r);
        }).WithName("GetReport").WithSummary("A saved or preset view/report definition");

        reports.MapGet("/{id}/run", async (string id, DateOnly? from, DateOnly? to, RunReportHandler h, CancellationToken ct) =>
        {
            var r = await h.HandleAsync(id, ct, from, to);
            return r is null ? Results.NotFound() : Results.Ok(r);
        }).WithName("RunReport").WithSummary("Run every panel of a view/report");

        reports.MapPost("/", async (
            [FromBody] SaveReportRequest request, SaveReportHandler h, IValidator<SaveReportRequest> v, CancellationToken ct) =>
        {
            var validation = await v.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var created = await h.CreateAsync(request, ct);
            return Results.Created($"/api/reports/{created.Id}", created);
        }).WithName("CreateReport").WithSummary("Save a view or report").RequireRateLimiting("write");

        reports.MapPut("/{id:guid}", async (
            Guid id, [FromBody] SaveReportRequest request, SaveReportHandler h, IValidator<SaveReportRequest> v, CancellationToken ct) =>
        {
            var validation = await v.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var updated = await h.UpdateAsync(id, request, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).WithName("UpdateReport").WithSummary("Update a saved view or report").RequireRateLimiting("write");

        reports.MapDelete("/{id:guid}", async (Guid id, SaveReportHandler h, CancellationToken ct) =>
                await h.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteReport").WithSummary("Delete a saved view or report").RequireRateLimiting("write");

        return app;
    }
}
