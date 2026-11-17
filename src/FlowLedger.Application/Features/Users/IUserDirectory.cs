namespace FlowLedger.Application.Features.Users;

/// <summary>An identity asserted by an external OpenID Connect provider (authentik, Keycloak, Entra, Google, ...).</summary>
public sealed record ExternalIdentity(string Issuer, string Subject, string? Email, string? DisplayName, bool EmailVerified = false);

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

    /// <summary>Every login (identity) that currently maps to the tenant. Several logins may share one tenant.</summary>
    Task<IReadOnlyList<LinkedIdentity>> ListIdentitiesAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Creates a short-lived single-use code. Signing in with another login and redeeming it links that login to this tenant.</summary>
    Task<LinkCode> CreateLinkCodeAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Moves the signed-in login (<paramref name="userId"/>) onto the tenant that issued <paramref name="code"/>.
    /// Nothing is deleted: data in the login's previous tenant stays in place (just no longer reachable from this login).
    /// </summary>
    Task<LinkOutcome> RedeemLinkCodeAsync(Guid userId, string code, CancellationToken ct = default);

    /// <summary>Detaches a login from a tenant. Refused for the last login so a tenant can never be orphaned.</summary>
    Task<bool> UnlinkAsync(Guid tenantId, Guid userId, CancellationToken ct = default);
}

public sealed record LinkedIdentity(Guid UserId, string Issuer, string Subject, string? Email, string? DisplayName, DateTimeOffset LastLoginUtc);

public sealed record LinkCode(string Code, DateTimeOffset ExpiresUtc);

public enum LinkOutcome { Linked, AlreadyLinked, InvalidOrExpired, UnknownUser }

/// <summary>Rules for who adopts the pre-multi-user household tenant.</summary>
/// <param name="LegacyTenantId">The tenant that already holds the household's data.</param>
/// <remarks>OwnerSubject and OwnerEmail accept several comma/semicolon-separated values: the household owner usually has several logins, and every one of them joins the legacy tenant.</remarks>
/// <param name="OwnerSubject">Preferred: the IdP subject (and optionally issuer) that owns it. Unforgeable by other users.</param>
/// <param name="OwnerIssuer">If set, the subject must come from this issuer.</param>
/// <param name="OwnerEmail">Alternative: this email, honoured only when the IdP reports it verified.</param>
/// <param name="AutoLinkVerifiedEmail">Opt-in (default off): a new login whose verified email equals an existing login's email joins that login's tenant.</param>
/// <param name="TrustUnverifiedEmail">Treat the email as verified even if the IdP omits email_verified (only for a self-hosted IdP you administer).</param>
/// <remarks>Fail closed: with none of the owner settings configured, nobody adopts the legacy tenant.</remarks>
public sealed record LegacyTenantClaim(Guid LegacyTenantId, string? OwnerEmail, string? OwnerSubject = null, string? OwnerIssuer = null,
    bool AutoLinkVerifiedEmail = false, bool TrustUnverifiedEmail = false);
