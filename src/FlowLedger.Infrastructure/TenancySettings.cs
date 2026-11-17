using FlowLedger.Application.Features.Users;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure;

/// <summary>Resolves which tenant holds the pre-multi-user household data, from config (shared by Api, Web, Worker).</summary>
public static class TenancySettings
{
    public static readonly Guid DefaultLegacyTenantId = new("00000000-0000-0000-0000-000000000001");

    public static LegacyTenantClaim Legacy(IConfiguration cfg)
    {
        var tenant = DefaultLegacyTenantId;
        foreach (var key in new[] { "Tenancy:LegacyTenantId", "Api:TenantId", "Worker:TenantId" })
        {
            if (Guid.TryParse(cfg[key], out var id) && id != Guid.Empty)
            {
                tenant = id;
                break;
            }
        }

        return new LegacyTenantClaim(tenant, NullIfBlank(cfg["Tenancy:LegacyOwnerEmail"]),
            NullIfBlank(cfg["Tenancy:LegacyOwnerSubject"]), NullIfBlank(cfg["Tenancy:LegacyOwnerIssuer"]),
            !bool.TryParse(cfg["Tenancy:AutoLinkVerifiedEmail"], out var auto) || auto,
            bool.TryParse(cfg["Tenancy:TrustUnverifiedEmail"], out var trust) && trust);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
