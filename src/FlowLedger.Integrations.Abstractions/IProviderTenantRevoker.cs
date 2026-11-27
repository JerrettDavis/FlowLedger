using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Integrations.Abstractions;

/// <summary>What a provider did when asked to drop everything it holds for a tenant (#189).</summary>
public enum ProviderRevocationOutcome
{
    /// <summary>The provider-side user, connections and credentials for the tenant were removed.</summary>
    Revoked,

    /// <summary>The provider holds nothing for this tenant (never connected, or already removed).</summary>
    NothingToRevoke,
}

/// <summary>
/// Optional capability: a provider whose API can delete the end user (and with it every bank connection and stored credential) it holds
/// for a tenant. Used when a tenant erases its data, so the aggregator is told to forget the user too. Providers with no such API
/// (SimpleFIN, Simulated) do not implement it; the host reports them as not supported. Implementations throw a provider exception on failure; the
/// caller treats revocation as best effort.
/// </summary>
public interface IProviderTenantRevoker
{
    Task<ProviderRevocationOutcome> RevokeTenantAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}
