using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// MX-specific endpoints layered on top of the generic /connect + /sync endpoints.
/// Active for both providers structurally, but only functional when Mx:Enabled=true
/// (the connect-token endpoint requires the concrete MX provider).
/// </summary>
internal static class MxEndpoints
{
    internal static IEndpointRouteBuilder MapMxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations/mx").WithTags("MX");

        // ── Connect token ──────────────────────────────────────────────────────────
        group.MapPost("/connect-token", async (
            IFinancialDataProvider primary,
            IConnectorRegistry connectors,
            ITenantContext tenant,
            IProviderConnectionStore connections,
            IHouseholdTenantAccess household,
            CancellationToken ct) =>
        {
            // Provisioning MX members is billable on the operator's key; only the household tenant may do it.
            if (!household.IsHouseholdTenant)
            {
                return Results.Problem(title: "Bank linking via MX is limited to the household owner", statusCode: StatusCodes.Status403Forbidden);
            }

            // With several connectors enabled the primary provider may not be MX: use the MX connector when it is on.
            var provider = connectors.IsEnabled(ConnectorIds.Mx) ? connectors.Resolve(ConnectorIds.Mx) : primary;
            if (provider is not MxFinancialDataProvider mx)
            {
                return Results.Problem(
                    detail: "The MX provider is not enabled (Mx:Enabled=false).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Reuse the persisted member if one exists: provisioning is billable and orphans members.
            var existing = await connections.GetMemberProviderIdAsync(mx.ProviderName, ct);
            ProviderMemberRef member;
            string widgetUrl;
            if (existing is not null)
            {
                widgetUrl = await mx.GetConnectWidgetUrlForMemberAsync(existing, ct);
                member = new ProviderMemberRef(existing, "Existing connection", ConnectionStatus.Connected);
            }
            else
            {
                (member, widgetUrl) = await mx.BeginConnectionWithWidgetAsync(TenantId.From(tenant.TenantId), ct);
                await connections.SaveAsync(mx.ProviderName, member.ProviderId, member.InstitutionName, member.Status.ToString(), ct);
            }

            return Results.Ok(new
            {
                memberId = member.ProviderId,
                institution = member.InstitutionName,
                status = member.Status.ToString(),
                widgetUrl,
            });
        })
        .WithName("MxConnectToken")
        .WithSummary("Begin an MX connection and return the Connect widget URL for the current tenant")
        .RequireAuthorization()        // explicit; also covered by the FallbackPolicy
        .RequireRateLimiting("write");

        // ── Webhook (shared pipeline, MX specifics only) ─────────────────────────
        group.MapWebhook("/webhooks", CreateWebhookPolicy())
            .WithName("MxWebhook")
            .WithSummary("Receive and verify an MX webhook, then enqueue a background sync");

        return app;
    }

    private static WebhookPolicy CreateWebhookPolicy() => new(
        ConnectorIds.Mx,
        // With several connectors enabled the primary provider may not be MX: use the MX connector when it is on.
        (connectors, primary) => connectors.IsEnabled(ConnectorIds.Mx) ? connectors.Resolve(ConnectorIds.Mx) : primary,
        request => request.Headers.TryGetValue("X-MX-Signature", out var sig) ? sig.ToString() : string.Empty,
        MemberKey: "memberId",
        UnknownOwnerLabel: "unknown member");
}
