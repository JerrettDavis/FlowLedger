using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.DataSafety;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Endpoints;

internal static class SyncEndpoints
{
    internal static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Sync");

        group.MapPost("/connect", async (IFinancialSyncService syncService, DataEnvironmentState env, CancellationToken ct) =>
        {
            if (env.Current == DataEnvironment.Production)
            {
                return Results.Problem(
                    title: "Not available in production",
                    detail: "The demo connect flow is disabled for production data. Use the provider's connect widget (/api/mx/connect-token).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var memberId = await syncService.ConnectAsync(ct);
            return Results.Ok(new { memberId, provider = "Simulated" });
        })
        .WithName("Connect")
        .WithSummary("Begin a provider connection (simulated by default) for the current tenant")
        .RequireRateLimiting("write");

        group.MapPost("/sync", async (IFinancialSyncService syncService, CancellationToken ct) =>
        {
            try
            {
                var result = await syncService.SyncAsync(ct);
                return Results.Ok(result);
            }
            catch (FlowLedger.Integrations.Abstractions.NeedsUserActionProviderException ex)
            {
                // Not a server fault: the user must (re)connect an institution first.
                return Results.Problem(
                    title: "Provider connection requires user action",
                    detail: ex.UserHint ?? ex.Message,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("Sync")
        .WithSummary("Run a full/incremental financial sync for the current tenant")
        .RequireRateLimiting("write");

        return app;
    }
}
