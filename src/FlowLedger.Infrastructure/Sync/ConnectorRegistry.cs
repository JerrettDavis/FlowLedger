using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>Scoped view of the connectors enabled on this instance (decided once, at startup, from configuration).</summary>
internal sealed class ConnectorRegistry(IServiceProvider services, IReadOnlyList<ConnectorDescriptor> enabled) : IConnectorRegistry
{
    public IReadOnlyList<ConnectorDescriptor> Enabled { get; } = enabled;

    public bool IsEnabled(ConnectorId connectorId) => Enabled.Any(c => c.Id == connectorId);

    public IFinancialDataProvider Resolve(ConnectorId connectorId)
    {
        var descriptor = Enabled.FirstOrDefault(c => c.Id == connectorId)
            ?? throw new InvalidOperationException($"Connector '{connectorId}' is not enabled on this instance.");
        return services.GetRequiredKeyedService<IFinancialDataProvider>(descriptor.Id.Value);
    }
}
