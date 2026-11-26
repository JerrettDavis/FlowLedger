using FlowLedger.Api.Jobs;
using FlowLedger.Application.Abstractions;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Quiltt;
using Microsoft.AspNetCore.Http;
using Quartz;

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

        group.MapPost("/webhooks", async (
                HttpRequest request,
                IConnectorRegistry connectors,
                IRawPayloadArchive archive,
                ISchedulerFactory schedulerFactory,
                IWebhookTenantResolver tenantResolver,
                IWebhookReplayStore? replayStore,
                ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                if (!connectors.IsEnabled(QuilttFinancialDataProvider.Name))
                {
                    return Results.NotFound();
                }

                var provider = connectors.Resolve(QuilttFinancialDataProvider.Name);
                var rawBody = await WebhookBody.ReadBoundedAsync(request.Body, WebhookBody.MaxBytes, ct);
                if (rawBody is null)
                {
                    FlowLedger.Infrastructure.Observability.FlowLedgerTelemetry.RecordWebhook(ConnectorIds.Quiltt, "too_large");
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                // Quiltt signs "1" + Quiltt-Timestamp + body; the verifier takes both headers as "t=<ts>,v1=<sig>".
                var signature = request.Headers.TryGetValue("Quiltt-Signature", out var sig)
                                && request.Headers.TryGetValue("Quiltt-Timestamp", out var ts)
                    ? $"t={ts},v1={sig}"
                    : string.Empty;
                try
                {
                    await provider.VerifyWebhookAsync(rawBody, signature, ct);
                }
                catch (InvalidWebhookSignatureException)
                {
                    FlowLedger.Infrastructure.Observability.FlowLedgerTelemetry.RecordWebhook(ConnectorIds.Quiltt, "invalid_signature");
                    return Results.Unauthorized();
                }

                if (await WebhookReplayGuard.SeenRecentlyAsync(rawBody, ConnectorIds.Quiltt, replayStore, ct, loggers.CreateLogger("WebhookReplay")))
                {
                    FlowLedger.Infrastructure.Observability.FlowLedgerTelemetry.RecordWebhook(ConnectorIds.Quiltt, "duplicate");
                    return Results.Accepted(value: new { duplicate = true });
                }

                var attributedTenant = await WebhookAttribution.TryResolveTenantAsync(provider, rawBody, tenantResolver, ct);
                await archive.ArchiveAsync(ConnectorIds.Quiltt, "Webhook", rawBody, new RawPayloadContext("POST", request.Path.Value ?? string.Empty, 200, attributedTenant), ct);

                ProviderWebhookEvent evt;
                try
                {
                    evt = await provider.ParseWebhookAsync(rawBody, ct);
                }
                catch (FatalProviderException ex)
                {
                    return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Malformed webhook payload");
                }

                if (!QuilttFinancialDataProvider.IsSyncTrigger(evt.EventType))
                {
                    return Results.Accepted(value: new { ignored = evt.EventType });
                }

                // The Quiltt profile id maps to exactly one tenant; unknown profiles are acknowledged and ignored (#150).
                var owner = await tenantResolver.ResolveAsync(QuilttFinancialDataProvider.Name, evt.MemberId, ct);
                if (owner is null)
                {
                    return Results.Accepted(value: new { ignored = "unknown profile", profileId = evt.MemberId });
                }

                var jobData = new JobDataMap
                {
                    [OnDemandSyncJob.TenantIdKey] = owner.Value.ToString(),
                    [OnDemandSyncJob.MemberIdKey] = evt.MemberId,
                    [OnDemandSyncJob.EventTypeKey] = evt.EventType,
                };
                var scheduler = await schedulerFactory.GetScheduler(ct);
                await scheduler.TriggerJob(OnDemandSyncJob.Key, jobData, ct);

                FlowLedger.Infrastructure.Observability.FlowLedgerTelemetry.RecordWebhook(ConnectorIds.Quiltt, "accepted");
                return Results.Accepted(value: new { received = evt.EventType, profileId = evt.MemberId });
            })
            .WithName("QuilttWebhook")
            .WithSummary("Receive and verify a Quiltt webhook, then enqueue a background sync")
            .AllowAnonymous() // the HMAC signature is the sole auth mechanism
            .RequireRateLimiting("webhook");

        return app;
    }

}
