using FlowLedger.Application.Abstractions;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Best-effort tenant attribution of an already signature-verified webhook body, used only to file its raw archive copy under the
/// right tenant partition (#142). It never changes how the webhook is handled: a body that cannot be parsed, or a member nobody
/// owns, simply stays unattributed, and the normal handler still parses and rejects it exactly as before.
/// </summary>
internal static class WebhookAttribution
{
    public static async Task<Guid?> TryResolveTenantAsync(
        IFinancialDataProvider provider, byte[] rawBody, IWebhookTenantResolver resolver, CancellationToken ct)
    {
        try
        {
            var evt = await provider.ParseWebhookAsync(rawBody, ct);
            return string.IsNullOrEmpty(evt.MemberId) ? null : await resolver.ResolveAsync(provider.ProviderName, evt.MemberId, ct);
        }
        catch (Exception ex) when (ex is FatalProviderException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
