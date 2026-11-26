using System.Security.Claims;
using FlowLedger.Web.Auth;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Auth;

/// <summary>#159: an open Blazor circuit re-applies the same login to tenant rule as the cookie validator; no new policy.</summary>
public sealed class TenantRevalidationTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static ClaimsPrincipal Signed(bool withClaims = true) =>
        new(new ClaimsIdentity(withClaims
            ? [new Claim(AuthSetup.TenantClaim, Tenant.ToString()), new Claim(AuthSetup.UserClaim, User.ToString())]
            : [], "cookie"));

    private static Func<Guid, CancellationToken, Task<IReadOnlyCollection<Guid>?>> Returns(params Guid[] users) =>
        (_, _) => Task.FromResult<IReadOnlyCollection<Guid>?>(users);

    [Fact]
    public async Task A_login_still_linked_to_its_tenant_keeps_the_circuit()
        => (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(), Returns(User))).Should().BeTrue();

    [Fact]
    public async Task A_login_no_longer_on_its_tenant_ends_the_session()
        => (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(), Returns(Guid.NewGuid()))).Should().BeFalse();

    [Fact]
    public async Task An_api_that_cannot_answer_never_signs_anyone_out()
    {
        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(), (_, _) => Task.FromResult<IReadOnlyCollection<Guid>?>(null))).Should().BeTrue();
        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(), (_, _) => throw new HttpRequestException("down"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_session_without_tenant_claims_is_rejected_like_the_cookie_validator_and_anonymous_is_left_alone()
    {
        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(withClaims: false), Returns(User))).Should().BeFalse();
        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(new ClaimsPrincipal(new ClaimsIdentity()), Returns())).Should().BeTrue();
    }
}
