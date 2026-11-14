using FlowLedger.Application.Features.Dashboard;

namespace FlowLedger.Api.Endpoints;

internal static class DashboardEndpoints
{
    internal static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard").RequireRateLimiting("api");

        group.MapGet("/activity", async (GetDashboardActivityHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(ct)))
            .WithName("GetDashboardActivity")
            .WithSummary("Recent, pending, large and suspicious transactions across all accounts");

        return app;
    }
}
