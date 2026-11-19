namespace FlowLedger.Application.Abstractions;

/// <summary>A provider-side account a connector has reported (whether or not it feeds a FlowLedger account yet).</summary>
public sealed record DiscoveredProviderAccount(
    string ConnectorId, string ProviderAccountId, string Name, string AccountType, decimal Balance, string Currency, DateTimeOffset LastSeenUtc);

/// <summary>
/// Per-tenant catalog of the provider accounts each connector reported on its last syncs. It lets the owner link an existing account to a
/// specific provider account (and see accounts a connector has found that are not linked), without calling the provider again.
/// </summary>
public interface IProviderAccountCatalog
{
    Task UpsertAsync(IReadOnlyCollection<DiscoveredProviderAccount> accounts, CancellationToken ct = default);

    Task<IReadOnlyList<DiscoveredProviderAccount>> ListAsync(string? connectorId = null, CancellationToken ct = default);
}
