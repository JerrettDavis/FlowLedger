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

        group.MapGet("/status", (IConfiguration configuration) =>
            {
                var build = BuildInfo.From(configuration);
                return Results.Ok(new { status = "ok", version = build.Version, commit = build.Commit });
            })
            .WithName("GetStatus")
            .WithSummary("Returns API status");

        return app;
    }
}
