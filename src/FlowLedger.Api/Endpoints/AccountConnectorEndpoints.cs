using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Request to feed an account from a connector's provider account, or pin it as manual (<c>connectorId = "manual"</c>).</summary>
public sealed record SetAccountConnectorRequest(string ConnectorId, string? ProviderAccountId);

/// <summary>
/// Per-account connector assignment (ADR 0010). Connectors are enabled per instance; here each account is linked to one connector's
/// provider account, pinned as manual (never synced), or left to the next sync to match.
/// </summary>
internal static class AccountConnectorEndpoints
{
    internal static IEndpointRouteBuilder MapAccountConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        // Which connector feeds each of the caller's accounts (empty for tenants with no linked accounts).
        app.MapGet("/api/accounts/connectors", async (IAccountConnectorLinkStore links, CancellationToken ct) =>
                Results.Ok((await links.ListAsync(ct)).Select(l => new { accountId = l.AccountId, connectorId = l.ConnectorId, providerAccountId = l.ProviderAccountId })))
            .RequireRateLimiting("api")
            .WithTags("Accounts")
            .WithName("ListAccountConnectors")
            .WithSummary("Which connector feeds each account");

        // Provider accounts a connector has reported that no FlowLedger account uses yet.
        app.MapGet("/api/integrations/connectors/{connectorId}/available-accounts", async (
                string connectorId,
                IConnectorRegistry registry,
                IAccountConnectorLinkStore links,
                IProviderAccountCatalog catalog,
                IHouseholdTenantAccess household,
                CancellationToken ct) =>
            {
                var denied = Check(registry, connectorId, household);
                if (denied is not null)
                {
                    return denied;
                }

                var linked = (await links.ListAsync(ct))
                    .Where(l => string.Equals(l.ConnectorId, connectorId, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.ProviderAccountId)
                    .ToHashSet(StringComparer.Ordinal);
                var available = (await catalog.ListAsync(connectorId, ct))
                    .Where(a => !linked.Contains(a.ProviderAccountId))
                    .Select(a => new { providerAccountId = a.ProviderAccountId, name = a.Name, accountType = a.AccountType, balance = a.Balance, currency = a.Currency, lastSeenUtc = a.LastSeenUtc });
                return Results.Ok(available);
            })
            .RequireRateLimiting("api")
            .WithTags("Integrations")
            .WithName("ListAvailableProviderAccounts")
            .WithSummary("Provider accounts a connector has seen that no account is linked to");

        app.MapPut("/api/accounts/{id:guid}/connector", async (
                Guid id,
                [FromBody] SetAccountConnectorRequest request,
                IAccountRepository accounts,
                IConnectorRegistry registry,
                IAccountConnectorLinkStore links,
                IProviderAccountCatalog catalog,
                IHouseholdTenantAccess household,
                CancellationToken ct) =>
            {
                if (await accounts.GetByIdAsync(AccountId.From(id), ct) is null)
                {
                    return Results.NotFound();
                }

                if (string.Equals(request.ConnectorId, AccountConnectorLinks.Manual, StringComparison.OrdinalIgnoreCase))
                {
                    await links.PinManualAsync(id, ct);
                    return Results.NoContent();
                }

                var denied = Check(registry, request.ConnectorId, household);
                if (denied is not null)
                {
                    return denied;
                }

                if (string.IsNullOrWhiteSpace(request.ProviderAccountId))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["providerAccountId"] = ["Choose which provider account feeds this account."] });
                }

                var known = (await catalog.ListAsync(request.ConnectorId, ct)).Any(a => a.ProviderAccountId == request.ProviderAccountId);
                if (!known)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["providerAccountId"] = ["That provider account has not been seen by this connector yet. Sync first."] });
                }

                return await links.LinkAsync(id, registry.Enabled.First(c => string.Equals(c.Id, request.ConnectorId, StringComparison.OrdinalIgnoreCase)).Id, request.ProviderAccountId, ct)
                    ? Results.NoContent()
                    : Results.Problem(title: "Already linked", detail: "That provider account already feeds another account.", statusCode: StatusCodes.Status409Conflict);
            })
            .RequireRateLimiting("write")
            .WithTags("Accounts")
            .WithName("SetAccountConnector")
            .WithSummary("Feed an account from a connector's provider account, or pin it as manual");

        app.MapDelete("/api/accounts/{id:guid}/connector", async (Guid id, IAccountConnectorLinkStore links, CancellationToken ct) =>
                await links.UnlinkAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireRateLimiting("write")
            .WithTags("Accounts")
            .WithName("ClearAccountConnector")
            .WithSummary("Remove an account's connector link (the next sync may match it again)");

        return app;
    }

    /// <summary>Null when the connector is enabled and the caller may use it; otherwise the error result.</summary>
    private static IResult? Check(IConnectorRegistry registry, string connectorId, IHouseholdTenantAccess household)
    {
        var descriptor = registry.Enabled.FirstOrDefault(c => string.Equals(c.Id, connectorId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["connectorId"] = [$"'{connectorId}' is not an enabled connector."] });
        }

        // Operator-held credentials serve the household tenant only.
        if (descriptor.SharedCredentials && !household.IsHouseholdTenant)
        {
            return Results.Problem(title: "This connector is limited to the household owner", statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }
}
