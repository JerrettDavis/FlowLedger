namespace FlowLedger.Application.Abstractions;

/// <summary>Read model of one stored provider connection (never contains secrets).</summary>
public sealed record ProviderConnectionInfo(
    string ProviderName, string Institution, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ConnectionIssueInfo? Issue = null);

/// <summary>
/// Persisted, connector-neutral description of what is wrong with a connection (mirrors Integrations.Abstractions.ProviderIssue,
/// kept as plain strings so Application stays independent of any connector). <see cref="Kind"/> is a ProviderIssueKind name.
/// </summary>
public sealed record ConnectionIssueInfo(
    string Kind, string Message, bool Retryable, bool RequiresUserAction,
    DateTimeOffset? RetryAfter, DateTimeOffset At, int ConsecutiveFailures);

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

    /// <summary>
    /// Records the outcome of the latest sync/health check for a provider connection: an issue (and bumps its consecutive-failure
    /// count) or null when the connection recovered (clears the issue and the count). No-op when no connection exists.
    /// </summary>
    Task RecordHealthAsync(string providerName, ConnectionIssueInfo? issue, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Persists a newly provisioned member (no-op if one already exists).</summary>
    Task SaveAsync(string providerName, string memberProviderId, string institution, string status, CancellationToken ct = default);
}
