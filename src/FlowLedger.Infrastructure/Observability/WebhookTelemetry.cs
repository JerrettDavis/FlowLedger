using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Observability;

/// <summary>OpenTelemetry-backed <see cref="IWebhookTelemetry"/>.</summary>
internal sealed class WebhookTelemetry : IWebhookTelemetry
{
    public void Record(ConnectorId connector, string outcome) => FlowLedgerTelemetry.RecordWebhook(connector.Value, outcome);
}
