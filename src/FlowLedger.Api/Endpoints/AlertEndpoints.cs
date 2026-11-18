using FlowLedger.Application.Features.Alerts;

namespace FlowLedger.Api.Endpoints;

/// <summary>Computed alerts: budgets at/over their limit and goals past their target date.</summary>
internal static class AlertEndpoints
{
    internal static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts").WithTags("Alerts").RequireRateLimiting("api");

        group.MapGet("/", async (GetAlertsHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ct)))
        .WithName("ListAlerts")
        .WithSummary("Budget and goal alerts");

        return app;
    }
}
