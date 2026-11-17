using FlowLedger.Application.Features.Users;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure;

/// <summary>Resolves which tenant holds the pre-multi-user household data, from config (shared by Api, Web, Worker).</summary>
public static class TenancySettings
{
    public static readonly Guid DefaultLegacyTenantId = new("00000000-0000-0000-0000-000000000001");

    public static LegacyTenantClaim Legacy(IConfiguration cfg)
    {
        foreach (var key in new[] { "Tenancy:LegacyTenantId", "Api:TenantId", "Worker:TenantId" })
        {
            if (Guid.TryParse(cfg[key], out var id) && id != Guid.Empty)
            {
                return new LegacyTenantClaim(id, NullIfBlank(cfg["Tenancy:LegacyOwnerEmail"]));
            }
        }

        return new LegacyTenantClaim(DefaultLegacyTenantId, NullIfBlank(cfg["Tenancy:LegacyOwnerEmail"]));
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
