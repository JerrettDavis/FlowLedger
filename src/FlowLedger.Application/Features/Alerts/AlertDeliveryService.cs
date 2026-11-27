using System.Security.Cryptography;
using System.Text;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Application.Features.Integrations;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Alerts;

/// <summary>How an alert delivery run went.</summary>
/// <param name="Detected">Alerts currently raised.</param>
/// <param name="Sent">New alerts handed to at least one channel (a subscribed webhook or the email channel).</param>
/// <param name="AlreadySent">Alerts delivered on an earlier run (each condition fires once per channel).</param>
/// <param name="Failed">New alerts no channel accepted; they are retried on the next run.</param>
public sealed record AlertDeliveryResult(int Detected, int Sent, int AlreadySent, int Failed);

/// <summary>
/// Delivers the alerts the household already gets in Home Assistant (bills due, large charges, price increases, new subscriptions, low
/// balances, budgets over their limit, goals falling behind) to the outbound webhooks subscribed to <see cref="IntegrationEvents.AlertRaised"/>
/// and, when the host configured one, to an email address (#70). Safe to call repeatedly: each alert is claimed per channel by its dedupe
/// key first, so it is delivered once per channel, and a delivery nobody accepted releases the claim so the next run retries. A tenant
/// with no subscribed webhook and no email channel costs one query.
/// </summary>
public sealed class AlertDeliveryService(
    IAlertEventSource alerts, IHomeAssistantStore store, IntegrationService integrations, ITenantContext tenant, IAlertEmailSender email)
{
    private const string ClaimPrefix = "webhook:";
    private const string EmailClaimPrefix = "email:";

    public async Task<AlertDeliveryResult> RunAsync(CancellationToken ct = default)
    {
        var webhooks = (await integrations.ListAsync(ct))
            .Any(i => i.Kind == IntegrationKind.Webhook && i.Enabled && i.Config.Events.Contains(IntegrationEvents.AlertRaised));
        var emailing = email.IsEnabled;
        if (!webhooks && !emailing)
        {
            return new AlertDeliveryResult(0, 0, 0, 0);
        }

        // The household's Home Assistant privacy settings apply: amounts and names are left out unless they chose to share them.
        var settings = await store.GetAsync(tenant.TenantId, ct);
        var events = await alerts.DetectAsync(alerts.Today, settings, ct);
        int sent = 0, already = 0, failed = 0;
        foreach (var e in events)
        {
            var delivered = false;
            var attempted = false;
            if (webhooks)
            {
                var claim = ClaimPrefix + e.DedupeKey;
                if (await store.TryClaimAsync(tenant.TenantId, claim, ct))
                {
                    attempted = true;
                    if (await integrations.DispatchAsync(IntegrationEvents.AlertRaised, HomeAssistantService.ToPayload(e), ct, DeliveryId(e.DedupeKey)) > 0)
                    {
                        delivered = true;
                    }
                    else
                    {
                        await store.ReleaseAsync(tenant.TenantId, claim, ct);
                    }
                }
            }

            if (emailing)
            {
                var claim = EmailClaimPrefix + e.DedupeKey;
                if (await store.TryClaimAsync(tenant.TenantId, claim, ct))
                {
                    attempted = true;
                    if (await email.SendAsync(ToEmail(e), ct) is null)
                    {
                        delivered = true;
                    }
                    else
                    {
                        await store.ReleaseAsync(tenant.TenantId, claim, ct);
                    }
                }
            }

            if (delivered)
            {
                sent++;
            }
            else if (attempted)
            {
                failed++;
            }
            else
            {
                already++;
            }
        }

        return new AlertDeliveryResult(events.Count, sent, already, failed);
    }

    /// <summary>The same alert always carries the same delivery id, so a receiver can drop a replay.</summary>
    private static Guid DeliveryId(string dedupeKey) => new(SHA256.HashData(Encoding.UTF8.GetBytes(dedupeKey))[..16]);

    private static AlertEmail ToEmail(HaEvent e)
    {
        // Alert text can carry merchant or account names; a line break in a subject would let that text add mail headers.
        var subject = "FlowLedger: " + e.Title.ReplaceLineEndings(" ").Trim();
        return new AlertEmail(subject, e.Message + Environment.NewLine, DeliveryId(e.DedupeKey));
    }
}
