using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>Scoped view of the connectors enabled on this instance (decided once, at startup, from configuration).</summary>
internal sealed class ConnectorRegistry(IServiceProvider services, IReadOnlyList<ConnectorDescriptor> enabled) : IConnectorRegistry
{
    public IReadOnlyList<ConnectorDescriptor> Enabled { get; } = enabled;

    public bool IsEnabled(string connectorId) =>
        Enabled.Any(c => string.Equals(c.Id, connectorId, StringComparison.OrdinalIgnoreCase));

    public IFinancialDataProvider Resolve(string connectorId)
    {
        var descriptor = Enabled.FirstOrDefault(c => string.Equals(c.Id, connectorId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Connector '{connectorId}' is not enabled on this instance.");
        return services.GetRequiredKeyedService<IFinancialDataProvider>(descriptor.Id);
    }
}
