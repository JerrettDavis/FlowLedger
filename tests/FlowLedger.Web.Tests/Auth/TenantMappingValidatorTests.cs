using System.Security.Claims;
using FlowLedger.Web.Auth;
using FluentAssertions;
using Outcome = FlowLedger.Web.Auth.TenantMappingValidator.Outcome;

namespace FlowLedger.Web.Tests.Auth;

public sealed class TenantMappingValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static ClaimsPrincipal Principal(Guid? tenant = null, Guid? user = null)
    {
        var claims = new List<Claim>();
        if (tenant is not null)
        {
            claims.Add(new Claim(AuthSetup.TenantClaim, tenant.Value.ToString()));
        }

        if (user is not null)
        {
            claims.Add(new Claim(AuthSetup.UserClaim, user.Value.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie"));
    }

    private static Func<Guid, CancellationToken, Task<IReadOnlyCollection<Guid>?>> Returns(params Guid[] users) =>
        (_, _) => Task.FromResult<IReadOnlyCollection<Guid>?>(users);

    [Fact]
    public async Task Recently_checked_sessions_are_not_rechecked()
    {
        var calls = 0;
        var outcome = await TenantMappingValidator.EvaluateAsync(Principal(Tenant, User), Now.AddMinutes(-1), Now,
            (_, _) => { calls++; return Task.FromResult<IReadOnlyCollection<Guid>?>([]); });

        outcome.Should().Be(Outcome.NotDue);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task A_login_still_on_its_tenant_is_valid() =>
        (await TenantMappingValidator.EvaluateAsync(Principal(Tenant, User), Now.AddMinutes(-6), Now, Returns(User, Guid.NewGuid())))
            .Should().Be(Outcome.Valid);

    [Fact]
    public async Task An_unlinked_or_moved_login_is_rejected() =>
        (await TenantMappingValidator.EvaluateAsync(Principal(Tenant, User), Now.AddMinutes(-6), Now, Returns(Guid.NewGuid())))
            .Should().Be(Outcome.Reject);

    [Fact]
    public async Task Never_checked_sessions_are_checked() =>
        (await TenantMappingValidator.EvaluateAsync(Principal(Tenant, User), null, Now, Returns()))
            .Should().Be(Outcome.Reject);

    [Fact]
    public async Task Cookie_without_tenant_or_user_claims_is_rejected()
    {
        (await TenantMappingValidator.EvaluateAsync(Principal(user: User), null, Now, Returns(User))).Should().Be(Outcome.Reject);
        (await TenantMappingValidator.EvaluateAsync(Principal(tenant: Tenant), null, Now, Returns(User))).Should().Be(Outcome.Reject);
    }

    [Fact]
    public async Task Api_outage_keeps_the_session()
    {
        (await TenantMappingValidator.EvaluateAsync(Principal(Tenant, User), null, Now,
            (_, _) => Task.FromResult<IReadOnlyCollection<Guid>?>(null))).Should().Be(Outcome.Unknown);
        (await TenantMappingValidator.EvaluateAsync(Principal(Tenant, User), null, Now,
            (_, _) => throw new HttpRequestException("down"))).Should().Be(Outcome.Unknown);
    }

    [Fact]
    public async Task The_tenant_from_the_cookie_is_the_one_queried()
    {
        Guid? asked = null;
        await TenantMappingValidator.EvaluateAsync(Principal(Tenant, User), null, Now,
            (t, _) => { asked = t; return Task.FromResult<IReadOnlyCollection<Guid>?>([User]); });
        asked.Should().Be(Tenant);
    }
}
