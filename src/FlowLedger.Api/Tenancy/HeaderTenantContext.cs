using FlowLedger.Api.Auth;
using FlowLedger.SharedKernel;
using Microsoft.Extensions.Options;

namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Production tenant context.
///
/// HTTP path (HttpContext present): reads TenantId from the X-Tenant-Id request header
/// and fails closed (throws <see cref="TenantResolutionException"/> → HTTP 401) when the
/// header is absent or unparseable. No demo fallback for real requests.
///
/// Background path (HttpContext absent, e.g. a Quartz job scope): there is no request to
/// fail closed on, so the context resolves to the single configured household tenant
/// (<c>Api:TenantId</c>, defaulting to the demo tenant). This keeps webhook-triggered
/// background sync working in Production while preserving the HTTP fail-closed guarantee.
/// </summary>
public sealed class HeaderTenantContext : ITenantContext
{
    public Guid TenantId { get; }
    public Guid UserId { get; }

    /// <summary>Per-request marker so a child scope created inside the same request does not re-consume the single-use nonce.</summary>
    private const string VerifiedItemKey = "FlowLedger.TenantAssertion.Verified";

    public HeaderTenantContext(
        IHttpContextAccessor accessor,
        IOptions<ApiOptions> apiOptions,
        TenantOverride tenantOverride,
        TenantNonceCache? nonces = null,
        TimeProvider? clock = null,
        IConfiguration? configuration = null)
    {
        var http = accessor.HttpContext;

        if (http is null)
        {
            // Background/job execution — no request to fail closed on. Run against the
            // single configured household tenant.
            // #135: the id may have been generated for a new install (stored in system_settings and surfaced as Tenancy:LegacyTenantId).
            var householdTenant = tenantOverride.TenantId
                ?? (configuration is null ? apiOptions.Value.EffectiveTenantId : FlowLedger.Infrastructure.TenancySettings.Legacy(configuration).LegacyTenantId);
            TenantId = householdTenant;
            UserId = householdTenant;
            return;
        }

        // HTTP request present — fail closed when the tenant header is missing/invalid.
        if (!http.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue)
            || !Guid.TryParse(headerValue, out var tenantId))
        {
            throw new TenantResolutionException();
        }

        // When a signing key is configured the Web app must vouch for this tenant (see TenantAssertion).
        // #135: when a signature is required, a missing signing key must reject (never skip) the check; startup also refuses that combination.
        var signingKey = apiOptions.Value.TenantSigningKey;
        if (apiOptions.Value.EffectiveRequireSignedTenant || !string.IsNullOrEmpty(signingKey))
        {
            var alreadyVerified = http.Items.TryGetValue(VerifiedItemKey, out var marker) && marker is Guid verified && verified == tenantId;
            if (string.IsNullOrEmpty(signingKey)
                || (!alreadyVerified && !TenantAssertion.Check(
                    signingKey,
                    tenantId,
                    http.Request.Headers["X-Tenant-Sig"].ToString(),
                    (clock ?? TimeProvider.System).GetUtcNow(),
                    http.Request.Method,
                    http.Request.PathBase.Add(http.Request.Path).Value ?? string.Empty,
                    apiOptions.Value.EffectiveAcceptLegacyTenantSignature,
                    nonces)))
            {
                throw new TenantResolutionException();
            }

            http.Items[VerifiedItemKey] = tenantId;
        }

        TenantId = tenantId;

        // Self-host assumption: one user per tenant. We do NOT require a second
        // X-User-Id header that callers don't yet know about.
        // TODO: replace with JWT sub claim in Phase 6.
        UserId = tenantId;
    }
}
