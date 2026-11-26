using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure;

/// <summary>The household tenant is the configured legacy tenant (<see cref="TenancySettings"/>); every other tenant is a guest.</summary>
internal sealed class HouseholdTenantAccess(ITenantContext tenant, IConfiguration configuration) : IHouseholdTenantAccess
{
    public bool IsHouseholdTenant => tenant.TenantId == TenancySettings.Legacy(configuration).LegacyTenantId;
}
