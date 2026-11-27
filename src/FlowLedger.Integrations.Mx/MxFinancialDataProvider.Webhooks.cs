using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Contracts;

namespace FlowLedger.Integrations.Mx;

// Webhook verification and parsing (#103: split out of MxFinancialDataProvider.cs).
public sealed partial class MxFinancialDataProvider
{
    public Task VerifyWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        string signatureHeader,
        CancellationToken cancellationToken = default)
    {
        _webhookVerifier.Verify(rawBody.Span, signatureHeader);
        return Task.CompletedTask;
    }

    public Task<ProviderWebhookEvent> ParseWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        CancellationToken cancellationToken = default)
    {
        MxWebhookPayload? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize(
                rawBody.Span, MxJsonContext.Default.MxWebhookPayload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            LogWebhookMalformed(ex);
            throw new FatalProviderException(
                "MX webhook payload is malformed and could not be parsed.", inner: ex);
        }

        if (payload is null)
        {
            throw new FatalProviderException("MX webhook payload could not be parsed.");
        }

        var memberId = payload.MemberGuid ?? payload.Member?.Guid ?? string.Empty;
        var eventType = FirstNonEmpty(payload.Type, payload.Action) ?? "UNKNOWN";
        var occurredAt = payload.CompletedAt is { } epoch
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : DateTimeOffset.UtcNow;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(metadata, "action", payload.Action);
        AddIfPresent(metadata, "user_guid", payload.UserGuid ?? payload.Member?.UserGuid);
        AddIfPresent(metadata, "account_guid", payload.AccountGuid);
        AddIfPresent(metadata, "connection_status",
            payload.ConnectionStatus ?? payload.Member?.ConnectionStatus);
        AddIfPresent(metadata, "completed_on", payload.CompletedOn);

        return Task.FromResult(new ProviderWebhookEvent(eventType, memberId, occurredAt, metadata));
    }
}
