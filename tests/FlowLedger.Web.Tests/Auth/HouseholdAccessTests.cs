using System.Security.Claims;
using FlowLedger.Web.Auth;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Auth;

public sealed class HouseholdAccessTests
{
    private static readonly OidcOptions On = new() { Authority = "https://idp.test/", ClientId = "fl" };

    private static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void Without_oidc_the_single_household_is_always_allowed() =>
        HouseholdAccess.IsHousehold(User(), new OidcOptions()).Should().BeTrue();

    [Fact]
    public void With_oidc_a_user_without_the_household_claim_is_refused() =>
        HouseholdAccess.IsHousehold(User(new Claim(AuthSetup.TenantClaim, Guid.NewGuid().ToString())), On).Should().BeFalse();

    [Fact]
    public void With_oidc_an_anonymous_user_is_refused() =>
        HouseholdAccess.IsHousehold(null, On).Should().BeFalse();

    [Fact]
    public void With_oidc_the_household_claim_grants_access() =>
        HouseholdAccess.IsHousehold(User(new Claim(HouseholdAccess.Claim, "true")), On).Should().BeTrue();
}
