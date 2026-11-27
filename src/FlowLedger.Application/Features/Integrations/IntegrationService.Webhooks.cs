using System.Globalization;
using System.Text.Json;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>Signed outbound webhooks: test, dispatch and the delivery log.</summary>
public sealed partial class IntegrationService
{
    // ── Webhooks ─────────────────────────────────────────────────────────────────

    /// <summary>Sends a harmless test event to a webhook so its receiver can be wired up.</summary>
    public async Task<(IntegrationCallResult? Result, string? Error)> TestWebhookAsync(Guid id, CancellationToken ct = default)
    {
        var integration = await store.GetAsync(TenantId, id, ct);
        if (integration is null || integration.Kind != IntegrationKind.Webhook)
        {
            return (null, "That webhook doesn't exist.");
        }

        var result = await DeliverAsync(integration, IntegrationEvents.Test, new Dictionary<string, object?> { ["message"] = "FlowLedger can reach this endpoint." }, ct);
        return (result, null);
    }

    /// <summary>
    /// Pushes an event to every enabled webhook subscribed to it and returns how many accepted it. Never throws: a broken receiver must not break the action that
    /// raised the event; each outcome is in the delivery log instead.
    /// </summary>
    public async Task<int> DispatchAsync(string eventKind, object? data, CancellationToken ct = default, Guid? deliveryId = null)
    {
        var sent = 0;
        IReadOnlyList<Integration> all;
        try
        {
            all = await store.ListAsync(TenantId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }

        foreach (var integration in all)
        {
            if (integration.Kind != IntegrationKind.Webhook || !integration.Enabled || !integration.Config.Events.Contains(eventKind))
            {
                continue;
            }

            // A receiver that accepts the connection and never answers must not hold up the import or sync that raised the event.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(DispatchTimeout);
            try
            {
                var delivered = await DeliverAsync(integration, eventKind, data, limit.Token, deliveryId);
                if (delivered.Error is null)
                {
                    sent++; // an HTTP error from the receiver is in the log but is not a delivery
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await RecordFailureAsync(integration, "event:" + eventKind, "The receiver did not answer in time.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Swallowed on purpose; see summary.
            }
        }

        return sent;
    }

    private async Task RecordFailureAsync(Integration integration, string trigger, string error)
    {
        try
        {
            await store.RecordDeliveryAsync(
                TenantId,
                new IntegrationDelivery(Guid.NewGuid(), integration.Id, _clock.GetUtcNow(), trigger, "POST",
                    IntegrationUrl.SafeTarget(integration.Config.Url), null, (int)DispatchTimeout.TotalMilliseconds, error),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The log is best effort.
        }
    }

    private async Task<IntegrationCallResult> DeliverAsync(Integration integration, string eventKind, object? data, CancellationToken ct, Guid? eventDeliveryId = null)
    {
        var secrets = await store.GetSecretsAsync(TenantId, integration.Id, ct);
        var deliveryId = (eventDeliveryId ?? Guid.NewGuid()).ToString("N");
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["source"] = "flowledger",
            ["version"] = 1,
            ["event"] = eventKind,
            ["event_id"] = deliveryId,
            ["occurred_at"] = _clock.GetUtcNow(),
            ["data"] = data,
        }, Json);

        var headers = OutboundHeaders.Build(integration.Config, secrets, new Dictionary<string, string>());
        var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        headers.Add(new(EventHeader, eventKind));
        headers.Add(new(DeliveryHeader, deliveryId));
        headers.Add(new(TimestampHeader, timestamp));
        if (secrets.SigningSecret is { } signing)
        {
            headers.Add(new(SignatureHeader, WebhookSigning.Sign(signing, timestamp, body)));
        }

        return await SendAndLogAsync(
            integration, "event:" + eventKind, new TransportRequest("POST", integration.Config.Url, headers, body, "application/json"), ct);
    }
}
