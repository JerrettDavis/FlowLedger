using FlowLedger.Application.Features.Dashboard;

namespace FlowLedger.Api.Endpoints;

internal static class DashboardEndpoints
{
    internal sealed record DismissSuspiciousRequest(string? Note);

    internal static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard").RequireRateLimiting("api");

        group.MapGet("/activity", async (GetDashboardActivityHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(ct)))
            .WithName("GetDashboardActivity")
            .WithSummary("Recent, pending, large and suspicious transactions across all accounts");

        group.MapPost("/suspicious/{transactionId:guid}/dismiss",
                async (Guid transactionId, DismissSuspiciousRequest? request, DismissSuspiciousHandler handler, CancellationToken ct) =>
                {
                    try
                    {
                        await handler.HandleAsync(transactionId, request?.Note, ct);
                        return Results.NoContent();
                    }
                    catch (ArgumentException ex)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = [ex.Message] });
                    }
                })
            .WithName("DismissSuspiciousTransaction")
            .WithSummary("Dismiss a flagged transaction from Suspicious Activity, with an optional note")
            .RequireRateLimiting("write");

        group.MapGet("/insights", async (GetDashboardInsightsHandler handler, DateOnly? from, DateOnly? to, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(from, to, ct)))
            .WithName("GetDashboardInsights")
            .WithSummary("Spending by category (this month), 6-month income vs spending, and net-worth trend from balance snapshots");

        return app;
    }
}
