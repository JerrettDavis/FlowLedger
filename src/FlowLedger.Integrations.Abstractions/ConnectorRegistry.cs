using FlowLedger.SharedKernel;

namespace FlowLedger.Integrations.Abstractions;

/// <summary>Describes one financial data connector (a provider implementation plus the credentials it runs with).</summary>
/// <param name="Id">Stable id; equals <see cref="IFinancialDataProvider.ProviderName"/> (e.g. "SimpleFIN", "MX", "Simulated").</param>
/// <param name="DisplayName">Human name for the UI.</param>
/// <param name="Kind">Short category for the UI ("Demo", "Aggregator (personal)", "Aggregator (widget)").</param>
/// <param name="SharedCredentials">
/// True when the connector runs on operator-held, instance-wide credentials. Such a connector may only act for the household
/// (legacy) tenant. False means the connector needs no shared secret (Simulated) or holds credentials per user.
/// </param>
/// <param name="Traits">The provider's declared <see cref="ConnectorTraits"/>, so host code can branch on behaviour without resolving the provider.</param>
public sealed record ConnectorDescriptor(ConnectorId Id, string DisplayName, string Kind, bool SharedCredentials, ConnectorTraits Traits = ConnectorTraits.None);

/// <summary>
/// The connectors enabled on this instance and a way to get the provider behind each. Enablement is configuration (per instance);
/// which accounts use which connector is data (per account, see account connector links).
/// </summary>
public interface IConnectorRegistry
{
    /// <summary>Enabled connectors, primary first (SimpleFIN, MX, then Simulated).</summary>
    IReadOnlyList<ConnectorDescriptor> Enabled { get; }

    bool IsEnabled(ConnectorId connectorId);

    /// <summary>The provider for an enabled connector. Throws <see cref="InvalidOperationException"/> for an unknown or disabled id.</summary>
    IFinancialDataProvider Resolve(ConnectorId connectorId);
}
