using System.Security.Cryptography;
using System.Text;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Application.Features.Integrations;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Alerts;

/// <summary>How an alert delivery run went.</summary>
/// <param name="Detected">Alerts currently raised.</param>
/// <param name="Sent">New alerts handed to at least one subscribed webhook.</param>
/// <param name="AlreadySent">Alerts delivered on an earlier run (each condition fires once).</param>
/// <param name="Failed">New alerts no subscribed webhook accepted; they are retried on the next run.</param>
public sealed record AlertDeliveryResult(int Detected, int Sent, int AlreadySent, int Failed);

/// <summary>
/// Delivers the alerts the household already gets in Home Assistant (bills due, large charges, price increases, new subscriptions, low
/// balances, budgets over their limit) to the outbound webhooks subscribed to <see cref="IntegrationEvents.AlertRaised"/> (#70).
/// Safe to call repeatedly: each alert is claimed by its dedupe key first, so it is delivered once, and a delivery nobody accepted
/// releases the claim so the next run retries. A tenant with no subscribed webhook costs one query.
/// </summary>
public sealed class AlertDeliveryService(
    IAlertEventSource alerts, IHomeAssistantStore store, IntegrationService integrations, ITenantContext tenant)
{
    private const string ClaimPrefix = "webhook:";

    public async Task<AlertDeliveryResult> RunAsync(CancellationToken ct = default)
    {
        var listening = (await integrations.ListAsync(ct))
            .Any(i => i.Kind == IntegrationKind.Webhook && i.Enabled && i.Config.Events.Contains(IntegrationEvents.AlertRaised));
        if (!listening)
        {
            return new AlertDeliveryResult(0, 0, 0, 0);
        }

        // The household's Home Assistant privacy settings apply: amounts and names are left out unless they chose to share them.
        var settings = await store.GetAsync(tenant.TenantId, ct);
        var events = await alerts.DetectAsync(alerts.Today, settings, ct);
        int sent = 0, already = 0, failed = 0;
        foreach (var e in events)
        {
            var claim = ClaimPrefix + e.DedupeKey;
            if (!await store.TryClaimAsync(tenant.TenantId, claim, ct))
            {
                already++;
                continue;
            }

            var accepted = await integrations.DispatchAsync(IntegrationEvents.AlertRaised, HomeAssistantService.ToPayload(e), ct, DeliveryId(e.DedupeKey));
            if (accepted > 0)
            {
                sent++;
            }
            else
            {
                failed++;
                await store.ReleaseAsync(tenant.TenantId, claim, ct);
            }
        }

        return new AlertDeliveryResult(events.Count, sent, already, failed);
    }

    /// <summary>The same alert always carries the same delivery id, so a receiver can drop a replay.</summary>
    private static Guid DeliveryId(string dedupeKey) => new(SHA256.HashData(Encoding.UTF8.GetBytes(dedupeKey))[..16]);
}
