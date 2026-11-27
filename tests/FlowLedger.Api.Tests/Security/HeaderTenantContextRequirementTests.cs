using FlowLedger.Api.Auth;
using FlowLedger.Api.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#135: with a signature required, a missing signing key rejects instead of silently skipping the check.</summary>
public sealed class HeaderTenantContextRequirementTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static HeaderTenantContext Context(bool required, string? key, string? sig)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Tenant-Id"] = Tenant.ToString();
        if (sig is not null)
        {
            http.Request.Headers["X-Tenant-Sig"] = sig;
        }

        var options = Options.Create(new ApiOptions { Key = "k", TenantSigningKey = key, EffectiveRequireSignedTenant = required });
        return new HeaderTenantContext(new HttpContextAccessor { HttpContext = http }, options, new TenantOverride());
    }

    private static DateTimeOffset Now => TimeProvider.System.GetUtcNow();

    [Fact]
    public void Required_without_a_signing_key_rejects_instead_of_skipping_the_check()
    {
        var act = () => Context(required: true, key: null, sig: null);

        act.Should().Throw<TenantResolutionException>();
    }

    [Fact]
    public void Required_with_a_key_accepts_only_a_valid_signature()
    {
        Context(required: true, key: "k-key", sig: TenantAssertion.Sign("k-key", Tenant, Now)).TenantId.Should().Be(Tenant);

        var wrongKey = () => Context(required: true, key: "k-key", sig: TenantAssertion.Sign("other", Tenant, Now));
        wrongKey.Should().Throw<TenantResolutionException>();

        var missing = () => Context(required: true, key: "k-key", sig: null);
        missing.Should().Throw<TenantResolutionException>();
    }

    [Fact]
    public void Not_required_and_no_key_keeps_the_legacy_unsigned_behaviour() =>
        Context(required: false, key: null, sig: null).TenantId.Should().Be(Tenant);

    [Fact]
    public void Blank_compose_passthroughs_bind_as_unset_instead_of_failing_startup()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:Key"] = "k",
            ["Api:RequireSignedTenant"] = "",
            ["Api:TenantSigningKey"] = "",
            ["Api:TenantId"] = "",
        }).Build();

        var bound = cfg.GetSection("Api").Get<ApiOptions>();

        bound!.EffectiveRequireSignedTenant.Should().BeFalse();
        bound.TenantId.Should().BeNull();
    }
}
