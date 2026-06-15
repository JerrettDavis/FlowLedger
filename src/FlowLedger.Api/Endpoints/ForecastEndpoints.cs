using FlowLedger.Application.Features.Forecasting;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class ForecastEndpoints
{
    internal static IEndpointRouteBuilder MapForecastEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/forecast").WithTags("Forecast")
            .RequireRateLimiting("api");

        group.MapGet("/", async (
            GetForecastHandler handler,
            [FromQuery] DateOnly? asOf,
            [FromQuery] DateOnly? from,
            [FromQuery] DateOnly? to,
            [FromQuery] int? months,
            [FromQuery] string? accounts,
            CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var effectiveAsOf = asOf ?? today;

            IReadOnlyList<Guid>? accountIds = null;
            if (!string.IsNullOrWhiteSpace(accounts))
            {
                var parsed = accounts
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                    .Where(g => g.HasValue)
                    .Select(g => g!.Value)
                    .ToList();
                if (parsed.Count > 0)
                {
                    accountIds = parsed;
                }
            }

            var query = new GetForecastQuery
            {
                AsOf = effectiveAsOf,
                HorizonStart = from,
                HorizonEnd = to,
                Months = months,
                AccountIds = accountIds
            };

            try
            {
                var result = await handler.HandleAsync(query, ct);
                // Map the domain model to the flattened wire contract. Returning the
                // domain ForecastResult directly serializes strongly-typed IDs / Money
                // as nested objects, which the Web client cannot deserialize into Guid/decimal.
                return Results.Ok(result.ToResponse());
            }
            catch (ForecastInputException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Forecast input error");
            }
        })
        .WithName("GetForecast")
        .WithSummary("Run a deterministic balance forecast for the current tenant");

        return app;
    }
}
