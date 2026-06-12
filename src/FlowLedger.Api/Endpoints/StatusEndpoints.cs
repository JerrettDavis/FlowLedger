namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Placeholder endpoint group — expanded in future milestones.
/// </summary>
internal static class StatusEndpoints
{
    internal static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Status")
            .RequireRateLimiting("api");

        group.MapGet("/status", () => Results.Ok(new { status = "ok", version = "0.1.0" }))
            .WithName("GetStatus")
            .WithSummary("Returns API status");

        return app;
    }
}
