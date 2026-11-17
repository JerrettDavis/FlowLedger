namespace FlowLedger.Application.Features.Users;

/// <summary>An identity asserted by an external OpenID Connect provider (authentik, Keycloak, Entra, Google, ...).</summary>
public sealed record ExternalIdentity(string Issuer, string Subject, string? Email, string? DisplayName);

/// <summary>A FlowLedger user and the tenant (data partition) it owns.</summary>
public sealed record ResolvedUser(Guid UserId, Guid TenantId, string? Email, string? DisplayName, bool Created);

/// <summary>
/// Maps an external (issuer, subject) identity to a FlowLedger user and tenant. Provider-agnostic: nothing here
/// knows which IdP is in use. Each user owns one tenant; all domain data is partitioned by tenant id.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// Returns the user for the identity, creating it (and its tenant) on first sight. The first user that qualifies
    /// (see <see cref="LegacyTenantClaim"/>) adopts the pre-existing single-household tenant so existing data is kept.
    /// </summary>
    Task<ResolvedUser> ResolveAsync(ExternalIdentity identity, CancellationToken ct = default);

    /// <summary>Every tenant that should be processed by background jobs (users' tenants plus the legacy tenant).</summary>
    Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default);
}

/// <summary>Rules for who adopts the pre-multi-user household tenant.</summary>
/// <param name="LegacyTenantId">The tenant that already holds the household's data.</param>
/// <param name="OwnerEmail">If set, only this email may adopt it; otherwise the first user to sign in does.</param>
public sealed record LegacyTenantClaim(Guid LegacyTenantId, string? OwnerEmail);
