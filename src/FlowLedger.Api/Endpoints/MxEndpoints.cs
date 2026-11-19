using System.Text.Json;
using FlowLedger.Api.Jobs;
using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Http;
using Quartz;

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
            HttpContext http,
            CancellationToken ct) =>
        {
            // Provisioning MX members is billable on the operator's key; only the household tenant may do it.
            if (!DataEndpoints.IsHouseholdTenant(http.RequestServices))
            {
                return Results.Problem(title: "Bank linking via MX is limited to the household owner", statusCode: StatusCodes.Status403Forbidden);
            }

            // With several connectors enabled the primary provider may not be MX: use the MX connector when it is on.
            var provider = connectors.IsEnabled("MX") ? connectors.Resolve("MX") : primary;
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

        // ── Webhook ──────────────────────────────────────────────────────────────
        group.MapPost("/webhooks", async (
            HttpRequest request,
            IFinancialDataProvider primary,
            IConnectorRegistry connectors,
            IRawPayloadArchive archive,
            ISchedulerFactory schedulerFactory,
            CancellationToken ct) =>
        {
            var provider = connectors.IsEnabled("MX") ? connectors.Resolve("MX") : primary;
            // Anonymous endpoint: bound the body BEFORE buffering it or doing any HMAC work.
            if (request.ContentLength > MaxWebhookBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // Read the raw body bytes — required for exact HMAC verification. Bounded read: chunked bodies
            // carry no Content-Length, so stop as soon as the cap is exceeded.
            var rawBody = await ReadBoundedAsync(request.Body, MaxWebhookBodyBytes, ct);
            if (rawBody is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var signature = request.Headers.TryGetValue("X-MX-Signature", out var sig)
                ? sig.ToString()
                : string.Empty;

            try
            {
                await provider.VerifyWebhookAsync(rawBody, signature, ct);
            }
            catch (InvalidWebhookSignatureException)
            {
                return Results.Unauthorized();
            }

            // Replay protection: a captured, validly signed body replayed within the window is acknowledged but not re-processed.
            if (WebhookReplayGuard.SeenRecently(rawBody))
            {
                return Results.Accepted(value: new { duplicate = true });
            }

            // Signature verified: retain the exact body before doing anything else with it.
            // (Unverified bodies are not archived — the endpoint is anonymous, so doing so would
            // let anyone fill the disk. A failed archive write surfaces as 500 so MX redelivers.)
            await archive.ArchiveAsync("MX", "Webhook", rawBody, new RawPayloadContext("POST", request.Path.Value ?? string.Empty, 200), ct);

            ProviderWebhookEvent evt;
            try
            {
                evt = await provider.ParseWebhookAsync(rawBody, ct);
            }
            catch (FatalProviderException ex)
            {
                // Provider translated the malformed payload into its domain exception.
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Malformed webhook payload");
            }
            catch (JsonException ex)
            {
                // Defensive catch: a provider that does not wrap JsonException internally still
                // produces a 400 here rather than a 500. Prefer FatalProviderException wrapping
                // in the provider, but this guard prevents unexpected 500s from leaking through.
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Malformed webhook payload");
            }

            // Enqueue a background sync via Quartz so the heavy work runs off the request path.
            // Pass the webhook context in the JobDataMap so OnDemandSyncJob can scope the sync
            // to the specific member when targeted per-member sync is supported.
            // Note: webhook-triggered syncs intentionally bypass the manual-refresh cooldown
            // (they are platform-initiated events, not user-initiated refreshes).
            var jobData = new JobDataMap
            {
                [OnDemandSyncJob.MemberIdKey] = evt.MemberId,
                [OnDemandSyncJob.EventTypeKey] = evt.EventType,
            };

            var scheduler = await schedulerFactory.GetScheduler(ct);
            await scheduler.TriggerJob(OnDemandSyncJob.Key, jobData, ct);

            return Results.Accepted(value: new { received = evt.EventType, memberId = evt.MemberId });
        })
        .WithName("MxWebhook")
        .WithSummary("Receive and verify an MX webhook, then enqueue a background sync")
        .AllowAnonymous()              // HMAC signature is the sole auth mechanism here
        .RequireRateLimiting("webhook");

        return app;
    }

    /// <summary>MX webhook payloads are small JSON events; anything larger is refused before signature work.</summary>
    internal const int MaxWebhookBodyBytes = 1024 * 1024;

    /// <summary>Reads at most <paramref name="limit"/> bytes; returns null as soon as the body exceeds it.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > limit)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }
}
