using FlowLedger.Application.Abstractions;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Read-only view of the financial data connectors (Simulated, SimpleFIN Bridge, MX): which exist, which are configured, which one
/// is active, their usage and this household's linked institutions. Never returns credentials or URLs that embed them.
/// Household-owner only, and switched off entirely with <c>Features:ConnectorsPage=false</c> (planned for production).
/// </summary>
internal static class ConnectorEndpoints
{
    private sealed record Descriptor(
        string Id, string DisplayName, string Kind, string Description,
        string[] ConfigKeys, Func<IConfiguration, bool> IsEnabled, Func<IConfiguration, bool> HasCredentials);

    private static bool Has(IConfiguration c, string key) => !string.IsNullOrWhiteSpace(c[key]);

    private static readonly Descriptor[] Catalog =
    [
        new("Simulated", "Simulated demo data", "Demo",
            "Deterministic fake accounts and transactions. No credentials, no cost. Used when no real connector is enabled.",
            [], _ => true, _ => true),
        new("SimpleFIN", "SimpleFIN Bridge", "Aggregator (personal)",
            "Personal bank aggregation through a SimpleFIN Bridge access URL. Institutions are linked at the Bridge, not in FlowLedger.",
            ["SimpleFin:Enabled", "SimpleFin:AccessUrl"],
            c => bool.TryParse(c["SimpleFin:Enabled"], out var on) && on,
            c => Has(c, "SimpleFin:AccessUrl")),
        new("MX", "MX", "Aggregator (widget)",
            "MX Platform API with the hosted connect widget. Billable per call; honours daily/monthly budgets and a kill switch.",
            ["Mx:Enabled", "Mx:ApiKey", "Mx:ClientId", "Mx:BaseUrl", "Mx:WebhookSecret"],
            c => bool.TryParse(c["Mx:Enabled"], out var on) && on,
            c => Has(c, "Mx:ApiKey") && Has(c, "Mx:ClientId") && Has(c, "Mx:BaseUrl") && Has(c, "Mx:WebhookSecret")),
    ];

    internal static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integrations/connectors", async (
                IConfiguration cfg,
                IFinancialDataProvider active,
                IProviderConnectionStore connections,
                IConnectorStatusReader status,
                CancellationToken ct) =>
            {
                if (!cfg.GetValue("Features:ConnectorsPage", true))
                {
                    return Results.NotFound();
                }

                var linked = await connections.ListAsync(ct);
                var items = new List<object>();
                foreach (var d in Catalog)
                {
                    var isActive = string.Equals(d.Id, active.ProviderName, StringComparison.OrdinalIgnoreCase);
                    var usage = await status.GetUsageAsync(d.Id, ct);
                    var lastSync = await status.LastSyncedAtAsync(d.Id, ct);
                    var isMx = d.Id == "MX";
                    items.Add(new
                    {
                        id = d.Id,
                        name = d.DisplayName,
                        kind = d.Kind,
                        description = d.Description,
                        active = isActive,
                        enabledInConfig = d.IsEnabled(cfg),
                        credentialsConfigured = d.HasCredentials(cfg),
                        configKeys = d.ConfigKeys,
                        capabilities = isActive ? active.Capabilities.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries) : null,
                        lastSyncedAt = lastSync,
                        usage = new
                        {
                            callsToday = usage.CallsToday,
                            callsThisMonth = usage.CallsThisMonth,
                            dailyBudget = isMx ? cfg.GetValue("Mx:Provider:DailyCallBudget", 500) : (int?)null,
                            monthlyBudget = isMx ? cfg.GetValue("Mx:Provider:MonthlyCallBudget", 5000) : (int?)null,
                            paused = isMx && cfg.GetValue("Mx:Provider:CallsPaused", false),
                        },
                        connections = linked
                            .Where(c => string.Equals(c.ProviderName, d.Id, StringComparison.OrdinalIgnoreCase))
                            .Select(c => new { institution = c.Institution, status = c.Status, linkedAt = c.CreatedAt, updatedAt = c.UpdatedAt })
                            .ToList(),
                    });
                }

                return Results.Ok(new
                {
                    activeProvider = active.ProviderName,
                    // Provider choice is process-wide and read at startup. Changing it needs a config change and a restart.
                    selectionMode = "configuration",
                    selectionNote = "The active connector is chosen by configuration at startup (SimpleFin:Enabled / Mx:Enabled; only one real connector at a time). Runtime selection and several feeds per user are planned (ADR 0009).",
                    connectors = items,
                });
            })
            .AddEndpointFilter(DataEndpoints.HouseholdTenantOnlyAsync)
            .RequireRateLimiting("api")
            .WithTags("Integrations")
            .WithName("ListConnectors")
            .WithSummary("Financial data connectors: configured, active, usage and linked institutions (household owner only)");

        return app;
    }
}
