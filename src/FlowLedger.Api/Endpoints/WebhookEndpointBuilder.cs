using System.Text.Json;
using FlowLedger.Api.Jobs;
using FlowLedger.Application.Abstractions;

using FlowLedger.Integrations.Abstractions;
using Quartz;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// What differs between providers' inbound webhooks (#94): which connector serves it, how its signature header(s) are read,
/// which events are worth a billable sync, and what the response calls the member. Everything else is the shared pipeline.
/// </summary>
internal sealed record WebhookPolicy(
    string ConnectorId,
    Func<IConnectorRegistry, IFinancialDataProvider, IFinancialDataProvider?> ResolveProvider,
    Func<HttpRequest, string> ReadSignature,
    string MemberKey,
    string UnknownOwnerLabel);

/// <summary>
/// The one anonymous-webhook pipeline for every provider: bounded body read, signature check (with the per-IP abuse budget),
/// replay guard, archive of the verified body, parse, event filter, tenant resolution, then an on-demand sync job.
/// </summary>
internal static class WebhookEndpointBuilder
{
    internal static RouteHandlerBuilder MapWebhook(this IEndpointRouteBuilder routes, string pattern, WebhookPolicy policy) =>
        routes.MapPost(pattern, (
                HttpRequest request,
                IFinancialDataProvider primary,
                IConnectorRegistry connectors,
                IRawPayloadArchive archive,
                ISchedulerFactory schedulerFactory,
                IWebhookTenantResolver tenantResolver,
                IWebhookReplayStore? replayStore,
                WebhookAbuseLimiter abuse,
                ILoggerFactory loggers,
                IWebhookTelemetry telemetry,
                CancellationToken ct) =>
            HandleAsync(policy, request, primary, connectors, archive, schedulerFactory, tenantResolver, replayStore, abuse, loggers, telemetry, ct))
            .AllowAnonymous() // the HMAC signature is the sole auth mechanism
            .RequireRateLimiting("webhook");

    private static async Task<IResult> HandleAsync(
        WebhookPolicy policy,
        HttpRequest request,
        IFinancialDataProvider primary,
        IConnectorRegistry connectors,
        IRawPayloadArchive archive,
        ISchedulerFactory schedulerFactory,
        IWebhookTenantResolver tenantResolver,
        IWebhookReplayStore? replayStore,
        WebhookAbuseLimiter abuse,
        ILoggerFactory loggers,
        IWebhookTelemetry telemetry,
        CancellationToken ct)
    {
        var connectorId = policy.ConnectorId;
        var provider = policy.ResolveProvider(connectors, primary);
        if (provider is null)
        {
            return Results.NotFound();
        }

        // Raw body bytes are required for exact HMAC verification. Bounded read: chunked bodies carry no Content-Length,
        // so stop as soon as the cap is exceeded.
        var rawBody = await WebhookBody.ReadBoundedAsync(request.Body, WebhookBody.MaxBytes, ct);
        if (rawBody is null)
        {
            telemetry.Record(connectorId, "too_large");
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            await provider.VerifyWebhookAsync(rawBody, policy.ReadSignature(request), ct);
        }
        catch (InvalidWebhookSignatureException)
        {
            telemetry.Record(connectorId, "invalid_signature");
            // Failed verifications spend a small per-(provider, IP) budget; verified deliveries never do (#152).
            return abuse.AllowInvalidResponse(connectorId, request.HttpContext.Connection.RemoteIpAddress?.ToString())
                ? Results.Unauthorized()
                : WebhookAbuse.TooManyRequests(request.HttpContext);
        }

        if (!abuse.AllowVerifiedDelivery(connectorId))
        {
            telemetry.Record(connectorId, "rate_limited");
            return WebhookAbuse.TooManyRequests(request.HttpContext);
        }

        // Replay protection: a captured, validly signed body replayed within the window is acknowledged but not re-processed.
        if (await WebhookReplayGuard.SeenRecentlyAsync(rawBody, connectorId, replayStore, ct, loggers.CreateLogger("WebhookReplay")))
        {
            telemetry.Record(connectorId, "duplicate");
            return Results.Accepted(value: new { duplicate = true });
        }

        // Signature verified: retain the exact body before doing anything else with it. (Unverified bodies are not archived:
        // the endpoint is anonymous, so doing so would let anyone fill the disk. A failed archive write surfaces as 500 so
        // the provider redelivers.)
        var attributedTenant = await WebhookAttribution.TryResolveTenantAsync(provider, rawBody, tenantResolver, ct);
        await archive.ArchiveAsync(connectorId, "Webhook", rawBody, new RawPayloadContext("POST", request.Path.Value ?? string.Empty, 200, attributedTenant), ct);

        ProviderWebhookEvent evt;
        try
        {
            evt = await provider.ParseWebhookAsync(rawBody, ct);
        }
        catch (Exception ex) when (ex is FatalProviderException or JsonException)
        {
            // The provider translated the malformed payload into its domain exception, or (defensively) let a JsonException
            // through: either way a 400 here rather than a 500.
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Malformed webhook payload");
        }

        // Only events that mean transactions/accounts changed are worth a billable sync (#120); the rest are acknowledged and ignored.
        if (!provider.IsWebhookSyncTrigger(evt.EventType))
        {
            return Results.Accepted(value: new { ignored = evt.EventType });
        }

        // The webhook is per member/profile: sync the tenant that owns it, and nobody if it is unknown (#150).
        var owner = await tenantResolver.ResolveAsync(provider.ProviderName, evt.MemberId, ct);
        if (owner is null)
        {
            return Results.Accepted(value: new Dictionary<string, string> { ["ignored"] = policy.UnknownOwnerLabel, [policy.MemberKey] = evt.MemberId });
        }

        // Webhook-triggered syncs intentionally bypass the manual-refresh cooldown (platform-initiated, not user-initiated);
        // the JobDataMap scopes the sync to the specific member.
        var jobData = new JobDataMap
        {
            [OnDemandSyncJob.TenantIdKey] = owner.Value.ToString(),
            [OnDemandSyncJob.MemberIdKey] = evt.MemberId,
            [OnDemandSyncJob.EventTypeKey] = evt.EventType,
        };

        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.TriggerJob(OnDemandSyncJob.Key, jobData, ct);

        telemetry.Record(connectorId, "accepted");
        return Results.Accepted(value: new Dictionary<string, string> { ["received"] = evt.EventType, [policy.MemberKey] = evt.MemberId });
    }
}
