using FlowLedger.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Endpoints;

internal static class SyncEndpoints
{
    internal static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Sync");

        group.MapPost("/connect", async (IFinancialSyncService syncService, CancellationToken ct) =>
        {
            var memberId = await syncService.ConnectAsync(ct);
            return Results.Ok(new { memberId, provider = "Simulated" });
        })
        .WithName("Connect")
        .WithSummary("Begin a provider connection (simulated by default) for the current tenant");

        group.MapPost("/sync", async (IFinancialSyncService syncService, CancellationToken ct) =>
        {
            var result = await syncService.SyncAsync(ct);
            return Results.Ok(result);
        })
        .WithName("Sync")
        .WithSummary("Run a full/incremental financial sync for the current tenant");

        return app;
    }
}
