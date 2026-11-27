using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Abstractions;

/// <summary>An account's data source: which connector feeds it and which provider-side account it mirrors.</summary>
/// <param name="ConnectorId">A connector id (see the connector registry) or <see cref="AccountConnectorLinks.Manual"/>.</param>
public sealed record AccountConnectorLink(Guid AccountId, ConnectorId ConnectorId, string ProviderAccountId, DateTimeOffset LinkedUtc);

public static class AccountConnectorLinks
{
    /// <summary>Pseudo-connector meaning "never synced": the user pinned this account as manual.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// Per-tenant link between an account and the connector that feeds it (ADR 0010). An account has at most one link and a provider
/// account is linked to at most one account. No link: the account is unmanaged (legacy accounts are matched and linked on sync).
/// </summary>
public interface IAccountConnectorLinkStore
{
    Task<IReadOnlyList<AccountConnectorLink>> ListAsync(CancellationToken ct = default);

    Task<AccountConnectorLink?> FindByProviderAccountAsync(ConnectorId connector, string providerAccountId, CancellationToken ct = default);

    Task<AccountConnectorLink?> FindByAccountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Links (or re-links) the account. Returns false when that provider account is already linked to a different account.</summary>
    Task<bool> LinkAsync(Guid accountId, ConnectorId connector, string providerAccountId, CancellationToken ct = default);

    /// <summary>Pins the account as manual: it stays out of every connector's sync.</summary>
    Task PinManualAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Removes the link. A legacy account will be matched and linked again by the next sync.</summary>
    Task<bool> UnlinkAsync(Guid accountId, CancellationToken ct = default);
}
