namespace FlowLedger.Application.Abstractions;

/// <summary>Read model of one stored provider connection (never contains secrets).</summary>
public sealed record ProviderConnectionInfo(string ProviderName, string Institution, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Durable per-tenant record of the provider member created at the provider. Callers must
/// consult this before provisioning: provisioning is billable and leaves orphans if repeated.
/// </summary>
public interface IProviderConnectionStore
{
    /// <summary>Every provider connection recorded for this tenant (any provider), newest first. No credentials are stored here.</summary>
    Task<IReadOnlyList<ProviderConnectionInfo>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProviderConnectionInfo>>([]);

    /// <summary>Returns the persisted member provider id for this tenant, or null if none.</summary>
    Task<string?> GetMemberProviderIdAsync(string providerName, CancellationToken ct = default);

    /// <summary>Persists a newly provisioned member (no-op if one already exists).</summary>
    Task SaveAsync(string providerName, string memberProviderId, string institution, string status, CancellationToken ct = default);
}
