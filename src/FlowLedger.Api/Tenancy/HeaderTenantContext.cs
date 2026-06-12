using FlowLedger.Api.Auth;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Http;
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

    public HeaderTenantContext(IHttpContextAccessor accessor, IOptions<ApiOptions> apiOptions)
    {
        var http = accessor.HttpContext;

        if (http is null)
        {
            // Background/job execution — no request to fail closed on. Run against the
            // single configured household tenant.
            var householdTenant = apiOptions.Value.EffectiveTenantId;
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

        TenantId = tenantId;

        // Self-host assumption: one user per tenant. We do NOT require a second
        // X-User-Id header that callers don't yet know about.
        // TODO: replace with JWT sub claim in Phase 6.
        UserId = tenantId;
    }
}
