namespace FlowLedger.Application.Abstractions;

/// <summary>Counts inbound provider webhooks by outcome (accepted, invalid_signature, duplicate, rate_limited, too_large) so endpoints need not know the metrics backend.</summary>
public interface IWebhookTelemetry
{
    void Record(string connectorId, string outcome);
}
