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
        string[] ConfigKeys, Func<IConfiguration, bool> IsEnabled, Func<IConfiguration, bool> HasCredentials, ConnectorTraits Traits = ConnectorTraits.None);

    private static bool Has(IConfiguration c, string key) => !string.IsNullOrWhiteSpace(c[key]);

    private static readonly Descriptor[] Catalog =
    [
        new(ConnectorIds.Simulated, "Simulated demo data", "Demo",
            "Deterministic fake accounts and transactions. No credentials, no cost. Used when no real connector is enabled.",
            [], _ => true, _ => true),
        new(ConnectorIds.SimpleFin, "SimpleFIN Bridge", "Aggregator (personal)",
            "Personal bank aggregation through a SimpleFIN Bridge access URL. Institutions are linked at the Bridge, not in FlowLedger.",
            ["SimpleFin:Enabled", "SimpleFin:AccessUrl"],
            c => bool.TryParse(c["SimpleFin:Enabled"], out var on) && on,
            c => Has(c, "SimpleFin:AccessUrl")),
        new(ConnectorIds.Mx, "MX", "Aggregator (widget)",
            "MX Platform API with the hosted connect widget. Billable per call; honours daily/monthly budgets and a kill switch.",
            ["Mx:Enabled", "Mx:ApiKey", "Mx:ClientId", "Mx:BaseUrl", "Mx:WebhookSecret"],
            c => bool.TryParse(c["Mx:Enabled"], out var on) && on,
            c => Has(c, "Mx:ApiKey") && Has(c, "Mx:ClientId") && Has(c, "Mx:BaseUrl") && Has(c, "Mx:WebhookSecret"),
            FlowLedger.Integrations.Mx.MxFinancialDataProvider.DeclaredTraits),
        new(ConnectorIds.Quiltt, "Quiltt", "Aggregator (embedded)",
            "In-app bank linking: each user links institutions inside FlowLedger through the Quiltt Connector (one Quiltt profile per tenant).",
            ["Quiltt:Enabled", "Quiltt:ApiKeySecret", "Quiltt:ConnectorId", "Quiltt:WebhookSecret"],
            c => bool.TryParse(c["Quiltt:Enabled"], out var on) && on,
            c => Has(c, "Quiltt:ApiKeySecret") && Has(c, "Quiltt:ConnectorId")), // WebhookSecret is optional: without it only webhooks are refused
    ];

    /// <summary>JSON shape of a connection's persisted issue (ADR 0018); the connection's <c>issue</c> is null when healthy.</summary>
    internal sealed record ConnectionIssueDto(
        string Kind, string Message, bool Retryable, bool RequiresUserAction,
        DateTimeOffset? RetryAfter, DateTimeOffset At, int ConsecutiveFailures, string? ProviderCode, string? RepairRef)
    {
        internal static ConnectionIssueDto From(ConnectionIssueInfo i) =>
            new(i.Kind, i.Message, i.Retryable, i.RequiresUserAction, i.RetryAfter, i.At, i.ConsecutiveFailures, i.ProviderCode, i.RepairRef);
    }

    internal static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        // The signed-in tenant's OWN connections and their health (no credentials). Unlike the connectors overview this is not
        // household-owner-only: a per-user connector (Quiltt) must be repairable by the user whose bank it is.
        app.MapGet("/api/integrations/connections", async (IProviderConnectionStore connections, CancellationToken ct) =>
                Results.Ok((await connections.ListAsync(ct)).Select(c => new
                {
                    provider = c.ProviderName,
                    institution = c.Institution,
                    status = c.Status,
                    issue = c.Issue is null ? null : ConnectionIssueDto.From(c.Issue),
                })))
            .WithName("MyConnections")
            .WithSummary("The current tenant's provider connections and their health");

        app.MapGet("/api/integrations/connectors", async (
                IConfiguration cfg,
                IConnectorRegistry registry,
                IFinancialDataProvider primary,
                IProviderConnectionStore connections,
                IConnectorStatusReader status,
                IAccountConnectorLinkStore links,
                IAccountRepository accounts,
                CancellationToken ct) =>
            {
                if (!cfg.GetValue("Features:ConnectorsPage", true))
                {
                    return Results.NotFound();
                }

                var linked = await connections.ListAsync(ct);
                var accountLinks = await links.ListAsync(ct);
                var accountsById = (await accounts.ListAsync(ct)).ToDictionary(a => a.Id, a => a);
                var items = new List<object>();
                foreach (var d in Catalog)
                {
                    var enabled = registry.IsEnabled(d.Id);
                    var isPrimary = string.Equals(d.Id, primary.ProviderName, StringComparison.OrdinalIgnoreCase);
                    var usage = await status.GetUsageAsync(d.Id, ct);
                    var lastSync = await status.LastSyncedAtAsync(d.Id, ct);
                    var isMx = d.Traits.HasFlag(ConnectorTraits.BillablePerCall);
                    items.Add(new
                    {
                        id = d.Id,
                        name = d.DisplayName,
                        kind = d.Kind,
                        description = d.Description,
                        active = enabled,
                        primary = isPrimary,
                        enabledInConfig = d.IsEnabled(cfg),
                        credentialsConfigured = d.HasCredentials(cfg),
                        configKeys = d.ConfigKeys,
                        capabilities = enabled
                            ? registry.Resolve(d.Id).Capabilities.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries)
                            : null,
                        accounts = accountLinks
                            .Where(l => string.Equals(l.ConnectorId, d.Id, StringComparison.OrdinalIgnoreCase) && accountsById.ContainsKey(l.AccountId))
                            .Select(l => new { accountId = l.AccountId, name = accountsById[l.AccountId].Name, providerAccountId = l.ProviderAccountId })
                            .ToList(),
                        lastSyncedAt = lastSync,
                        usage = new
                        {
                            callsToday = usage.CallsToday,
                            callsThisMonth = usage.CallsThisMonth,
                            dailyBudget = isMx ? cfg.GetValue("Mx:Provider:DailyCallBudget", 500) : (int?)null,
                            monthlyBudget = isMx ? cfg.GetValue("Mx:Provider:MonthlyCallBudget", ApiLimits.DefaultMxMonthlyCallBudget) : (int?)null,
                            paused = isMx && cfg.GetValue("Mx:Provider:CallsPaused", false),
                        },
                        connections = linked
                            .Where(c => string.Equals(c.ProviderName, d.Id, StringComparison.OrdinalIgnoreCase))
                            .Select(c => new
                            {
                                institution = c.Institution,
                                status = c.Status,
                                linkedAt = c.CreatedAt,
                                updatedAt = c.UpdatedAt,
                                issue = c.Issue is null ? null : ConnectionIssueDto.From(c.Issue),
                            })
                            .ToList(),
                    });
                }

                // Accounts that no connector feeds: manual, pinned manual, or not yet matched by a sync.
                var linkedIds = accountLinks.Where(l => l.ConnectorId != AccountConnectorLinks.Manual).Select(l => l.AccountId).ToHashSet();
                var unassigned = accountsById.Values
                    .Where(a => !linkedIds.Contains(a.Id))
                    .Select(a => new { accountId = a.Id, name = a.Name, pinnedManual = accountLinks.Any(l => l.AccountId == a.Id && l.ConnectorId == AccountConnectorLinks.Manual) })
                    .ToList();

                return Results.Ok(new
                {
                    activeProvider = primary.ProviderName,
                    // Connectors are enabled per instance by configuration (restart to change); each account is linked to one connector (ADR 0010).
                    selectionMode = "per-account",
                    selectionNote = "Connectors are enabled for the whole instance by configuration (SimpleFin:Enabled, Mx:Enabled; restart to change). Each account is linked to one connector, or none for a manual account.",
                    connectors = items,
                    unassignedAccounts = unassigned,
                });
            })
            .AddEndpointFilter<HouseholdTenantOnlyFilter>()
            .RequireRateLimiting("api")
            .WithTags("Integrations")
            .WithName("ListConnectors")
            .WithSummary("Financial data connectors: configured, active, usage and linked institutions (household owner only)");

        return app;
    }
}
