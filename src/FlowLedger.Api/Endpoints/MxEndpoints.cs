using FlowLedger.Api.Jobs;
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
            IFinancialDataProvider provider,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            if (provider is not MxFinancialDataProvider mx)
            {
                return Results.Problem(
                    detail: "The MX provider is not enabled (Mx:Enabled=false).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var (member, widgetUrl) = await mx.BeginConnectionWithWidgetAsync(
                TenantId.From(tenant.TenantId), ct);

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
            IFinancialDataProvider provider,
            ISchedulerFactory schedulerFactory,
            CancellationToken ct) =>
        {
            // Read the raw body bytes — required for exact HMAC verification.
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, ct);
            var rawBody = ms.ToArray();

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

            var evt = await provider.ParseWebhookAsync(rawBody, ct);

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
}
