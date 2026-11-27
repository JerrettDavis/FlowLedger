using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Integrations.Abstractions;
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
            return Results.Ok(new { memberId, provider = ConnectorIds.Simulated });
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

        // Lets the browser decide whether an auto-sync is due without calling the provider.
        group.MapGet("/sync/status", async (
                IConnectorRegistry connectors,
                IConnectorStatusReader status,
                FlowLedger.Integrations.Abstractions.IFinancialDataProvider primary,
                IConfiguration cfg,
                CancellationToken ct) =>
            {
                // Last successful sync across every enabled connector (each syncs on the same schedule).
                DateTimeOffset? last = null;
                foreach (var c in connectors.Enabled)
                {
                    var t = await status.LastSyncedAtAsync(c.Id, ct);
                    if (t is { } v && (last is null || v > last))
                    {
                        last = v;
                    }
                }

                var anyReal = connectors.Enabled.Any(c => !c.Traits.HasFlag(ConnectorTraits.SeedsDemoData));
                var minutes = int.TryParse(cfg["Sync:MinIntervalMinutes"], out var m) ? m : (anyReal ? 30 : 0);
                return Results.Ok(new { lastSyncedAt = last, minIntervalMinutes = minutes, provider = primary.ProviderName });
            })
            .WithName("GetSyncStatus")
            .WithSummary("When the last successful sync finished and the enforced minimum interval");

        // "Would make N calls" before a billable sync: one account listing plus one transactions page per active account.
        group.MapGet("/sync/estimate", async (IAccountRepository accounts, CancellationToken ct) =>
            {
                var active = (await accounts.ListAsync(ct)).Count;
                return Results.Ok(new { activeAccounts = active, estimatedProviderCalls = SyncCallEstimate.ForAccounts(active) });
            })
            .WithName("GetSyncEstimate")
            .WithSummary("How many provider calls a sync would make right now (no provider call is made)");

        group.MapPost("/sync", async (IFinancialSyncService syncService, HttpContext httpContext, CancellationToken ct) =>
        {
            try
            {
                var result = await syncService.SyncAsync(ct);
                return Results.Ok(result);
            }
            catch (SyncThrottledException ex)
            {
                httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(ex.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Problem(title: "Synced recently", detail: ex.Message, statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (SyncAlreadyRunningException ex)
            {
                return Results.Problem(title: "Sync already running", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            // Provider failures (user action, rate limit, outage, call budget) are mapped centrally by ProviderExceptionHandler.
        })
        .WithName("Sync")
        .WithSummary("Run a full/incremental financial sync for the current tenant")
        .RequireRateLimiting("write");

        return app;
    }
}
