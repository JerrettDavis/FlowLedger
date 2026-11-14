using FlowLedger.Infrastructure.DataSafety;

namespace FlowLedger.Api.Endpoints;

/// <summary>Tells the UI which data environment this deployment is verified to be running (demo/sandbox/production).</summary>
internal static class EnvironmentEndpoints
{
    internal static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api").WithTags("Status").RequireRateLimiting("api")
            .MapGet("/environment", (DataEnvironmentState state) =>
            {
                var env = state.Current?.ToString().ToLowerInvariant() ?? "unknown";
                return Results.Ok(new { environment = env, isProduction = state.Current == DataEnvironment.Production });
            })
            .WithName("GetEnvironment")
            .WithSummary("Verified data environment of this deployment");
        return app;
    }
}
