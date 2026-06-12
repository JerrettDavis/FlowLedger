using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Development-mode tenant context resolved from the X-Tenant-Id request header.
/// Falls back to a fixed demo tenant when the header is absent.
///
/// SEAM: Replace this with a JWT-claims-based tenant resolver once auth is wired
/// in Milestone 5. The interface contract is identical — just swap the DI registration.
/// </summary>
public sealed class DevTenantContext : ITenantContext
{
    /// <summary>Fixed demo tenant used when no X-Tenant-Id header is present.</summary>
    public static readonly Guid DemoTenantId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Fixed demo user id for the dev tenant.</summary>
    public static readonly Guid DemoUserId = new("00000000-0000-0000-0000-000000000002");

    public Guid TenantId { get; }
    public Guid UserId { get; }

    public DevTenantContext(IHttpContextAccessor accessor)
    {
        var http = accessor.HttpContext;

        if (http is not null &&
            http.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue) &&
            Guid.TryParse(headerValue, out var parsed))
        {
            TenantId = parsed;
        }
        else
        {
            TenantId = DemoTenantId;
        }

        UserId = DemoUserId;
    }
}
