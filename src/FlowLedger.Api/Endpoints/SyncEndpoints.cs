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

        // Which provider is active and, for providers with no embeddable widget, where the user links institutions.
        group.MapGet("/integrations/provider", (FlowLedger.Integrations.Abstractions.IFinancialDataProvider provider) =>
            {
                var name = provider.ProviderName;
                var simplefin = name.Contains("simplefin", StringComparison.OrdinalIgnoreCase);
                return Results.Ok(new
                {
                    provider = name,
                    usesWidget = !simplefin,
                    manageUrl = simplefin ? "https://bridge.simplefin.org/simplefin/" : null,
                });
            })
            .WithName("GetProviderInfo")
            .WithSummary("Active data provider and where institutions are linked");

        group.MapPost("/sync", async (IFinancialSyncService syncService, HttpContext httpContext, CancellationToken ct) =>
        {
            try
            {
                var result = await syncService.SyncAsync(ct);
                return Results.Ok(result);
            }
            catch (SyncThrottledException ex)
            {
                httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(ex.RetryAfter.TotalSeconds)).ToString();
                return Results.Problem(title: "Synced recently", detail: ex.Message, statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (SyncAlreadyRunningException ex)
            {
                return Results.Problem(title: "Sync already running", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (FlowLedger.Integrations.Abstractions.ProviderCallBudgetExceededException ex)
            {
                return Results.Problem(title: "Provider call budget reached", detail: ex.Message, statusCode: StatusCodes.Status429TooManyRequests);
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
