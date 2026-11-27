using FlowLedger.Application.Abstractions;

namespace FlowLedger.Infrastructure.Observability;

/// <summary>OpenTelemetry-backed <see cref="IWebhookTelemetry"/>.</summary>
internal sealed class WebhookTelemetry : IWebhookTelemetry
{
    public void Record(string connectorId, string outcome) => FlowLedgerTelemetry.RecordWebhook(connectorId, outcome);
}
