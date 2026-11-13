namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Durable per-tenant record of the provider member created at the provider. Callers must
/// consult this before provisioning: provisioning is billable and leaves orphans if repeated.
/// </summary>
public interface IProviderConnectionStore
{
    /// <summary>Returns the persisted member provider id for this tenant, or null if none.</summary>
    Task<string?> GetMemberProviderIdAsync(string providerName, CancellationToken ct = default);

    /// <summary>Persists a newly provisioned member (no-op if one already exists).</summary>
    Task SaveAsync(string providerName, string memberProviderId, string institution, string status, CancellationToken ct = default);
}
