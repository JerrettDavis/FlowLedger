using FlowLedger.Application.Features.Insights;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class InsightsEndpoints
{
    internal static IEndpointRouteBuilder MapInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/insights").WithTags("Insights").RequireRateLimiting("api");

        group.MapGet("/breakdown", async (
                GetSpendingBreakdownHandler handler,
                [FromQuery] DateOnly? from,
                [FromQuery] DateOnly? to,
                [FromQuery] string? accounts,
                [FromQuery] string? type,
                [FromQuery] string? groupBy,
                [FromQuery] string? parent,
                [FromQuery] string? select,
                [FromQuery] string? bucket,
                [FromQuery] bool? includeTransfers,
                CancellationToken ct) =>
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var end = to ?? today;
                var start = from ?? new DateOnly(end.Year, end.Month, 1);
                if (end < start)
                {
                    return Results.Problem(title: "Invalid range", detail: "'to' must be on or after 'from'.", statusCode: 400);
                }

                if ((end.DayNumber - start.DayNumber) > 366 * 5)
                {
                    return Results.Problem(title: "Range too large", detail: "Choose a range of 5 years or less.", statusCode: 400);
                }

                HashSet<Guid>? ids = null;
                if (!string.IsNullOrWhiteSpace(accounts))
                {
                    ids = accounts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                        .Where(g => g != Guid.Empty)
                        .ToHashSet();
                }

                var by = Enum.TryParse<BreakdownGroupBy>(groupBy, ignoreCase: true, out var parsed) ? parsed : BreakdownGroupBy.Category;
                var b = bucket is "day" or "week" ? bucket : "month";

                var result = await handler.HandleAsync(new BreakdownQuery(
                    start, end, ids, string.Equals(type, "income", StringComparison.OrdinalIgnoreCase),
                    by, parent, select, b, includeTransfers == true), ct);
                return Results.Ok(result);
            })
            .WithName("GetSpendingBreakdown")
            .WithSummary("Spending/income broken down by category (drill-down), tag, merchant or account, with trend and transactions");

        return app;
    }
}
