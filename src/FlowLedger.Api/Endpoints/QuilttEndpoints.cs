using FlowLedger.Application.Abstractions;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Quiltt;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Quiltt embedded-connector endpoints. Unlike MX/SimpleFIN every tenant may link its own banks: each tenant maps to one Quiltt
/// profile, and the browser receives only a short-lived session token for that profile (the API key stays server-side).
/// </summary>
internal static class QuilttEndpoints
{
    internal static IEndpointRouteBuilder MapQuilttEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations/quiltt").WithTags("Quiltt");

        // Lets the UI decide whether to offer the in-app Connector. Never exposes secrets.
        group.MapGet("/config", (IConnectorRegistry connectors) =>
            {
                if (!connectors.IsEnabled(QuilttFinancialDataProvider.Name)
                    || connectors.Resolve(QuilttFinancialDataProvider.Name) is not QuilttFinancialDataProvider q)
                {
                    return Results.Ok(new { enabled = false });
                }

                return Results.Ok(new { enabled = true, connectorId = q.ConnectorId, scriptUrl = q.ConnectorScriptUrl });
            })
            .WithName("QuilttConfig")
            .WithSummary("Whether the in-app Quiltt Connector is available");

        group.MapPost("/session", async (
                IConnectorRegistry connectors,
                IProviderConnectionStore connections,
                FlowLedger.Api.Auth.SignUpPolicy signUp,
                IHouseholdTenantAccess household,
                CancellationToken ct) =>
            {
                if (!connectors.IsEnabled(QuilttFinancialDataProvider.Name)
                    || connectors.Resolve(QuilttFinancialDataProvider.Name) is not QuilttFinancialDataProvider quiltt)
                {
                    return Results.Problem(detail: "Quiltt is not enabled (Quiltt:Enabled=false).", statusCode: StatusCodes.Status409Conflict);
                }

                if (string.IsNullOrWhiteSpace(quiltt.ConnectorId))
                {
                    return Results.Problem(detail: "Quiltt:ConnectorId is not configured.", statusCode: StatusCodes.Status409Conflict);
                }

                // One profile per tenant: reuse it, create it only the first time.
                var existing = await connections.GetMemberProviderIdAsync(quiltt.ProviderName, ct);

                // #145: creating a profile is billable on the operator's account. With open sign-up the tenant may belong to a stranger, so
                // only the household may create one unless Quiltt:AllowOpenSignupTenants=true. Existing profiles are reused as before.
                if (existing is null && signUp.BlocksUninvitedBillableProvisioning && !household.IsHouseholdTenant)
                {
                    return Results.Problem(
                        title: "Bank linking via Quiltt is limited to invited tenants while sign-up is open (Quiltt:AllowOpenSignupTenants)",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var session = await quiltt.CreateSessionAsync(existing, ct);
                if (existing is null)
                {
                    await connections.SaveAsync(quiltt.ProviderName, session.ProfileId, "Quiltt", ConnectionStatus.ConnectionPending.ToString(), ct);
                }

                return Results.Ok(new
                {
                    connectorId = quiltt.ConnectorId,
                    scriptUrl = quiltt.ConnectorScriptUrl,
                    token = session.Token,
                    profileId = session.ProfileId,
                    expiresAt = session.ExpiresAt,
                });
            })
            .WithName("QuilttSession")
            .WithSummary("Mint a short-lived Quiltt session token so the browser can open the in-app Connector")
            .RequireAuthorization()
            .RequireRateLimiting("write");

        group.MapWebhook("/webhooks", CreateWebhookPolicy())
            .WithName("QuilttWebhook")
            .WithSummary("Receive and verify a Quiltt webhook, then enqueue a background sync");

        return app;
    }

    private static WebhookPolicy CreateWebhookPolicy() => new(
        ConnectorIds.Quiltt,
        (connectors, _) => connectors.IsEnabled(QuilttFinancialDataProvider.Name) ? connectors.Resolve(QuilttFinancialDataProvider.Name) : null,
        // Quiltt signs "1" + Quiltt-Timestamp + body; the verifier takes both headers as "t=<ts>,v1=<sig>".
        request => request.Headers.TryGetValue("Quiltt-Signature", out var sig) && request.Headers.TryGetValue("Quiltt-Timestamp", out var ts)
            ? $"t={ts},v1={sig}"
            : string.Empty,
        MemberKey: "profileId",
        UnknownOwnerLabel: "unknown profile");
}
