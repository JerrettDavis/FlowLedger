using FluentAssertions;
using FlowLedger.Infrastructure.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.Tests.Configuration;

public sealed class HouseholdTenantAccessTests
{
    private static readonly Guid Household = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Tenancy:LegacyTenantId"] = Household.ToString() })
        .Build();

    [Fact]
    public void The_configured_legacy_tenant_is_the_household()
        => new HouseholdTenantAccess(new TestTenantContext(Household), Config()).IsHouseholdTenant.Should().BeTrue();

    [Fact]
    public void Any_other_tenant_is_a_guest()
        => new HouseholdTenantAccess(TestTenantContext.New(), Config()).IsHouseholdTenant.Should().BeFalse();

    [Fact]
    public void Without_configuration_the_default_legacy_tenant_is_the_household()
    {
        var empty = new ConfigurationBuilder().Build();

        new HouseholdTenantAccess(new TestTenantContext(TenancySettings.DefaultLegacyTenantId), empty).IsHouseholdTenant.Should().BeTrue();
    }
}
