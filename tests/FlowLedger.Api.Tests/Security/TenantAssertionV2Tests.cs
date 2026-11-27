using FlowLedger.Api.Auth;
using FlowLedger.Api.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#135: v2 tenant assertion bound to method and path, single-use nonce, v1 accepted behind a flag.</summary>
public sealed class TenantAssertionV2Tests
{
    private const string Key = "test-signing-key";
    private const string Nonce = "000102030405060708090a0b0c0d0e0f";
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Same literal as FlowLedger.Web.Tests TenantAssertionSignerTests: if either side changes the format, both tests fail.</summary>
    private const string GoldenV2 = "v2.1795003200.000102030405060708090a0b0c0d0e0f.61d03c130037c0e18e14b229d0309d627b5adb740ea27982f29fd8c1a4d824e0";

    private const string GoldenV1 = "1795003200.b246c9a5dc891a3e72c9a0c8a72bacac676ebb24907efe88406b7b42286cb399";

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void V2_matches_the_golden_vector_the_Web_signer_also_checks()
        => TenantAssertion.SignV2(Key, Tenant, Now, "POST", "/api/accounts", Nonce).Should().Be(GoldenV2);

    [Fact]
    public void V1_golden_vector_is_unchanged()
        => TenantAssertion.Sign(Key, Tenant, Now).Should().Be(GoldenV1);

    [Fact]
    public void V2_verifies_for_the_same_method_and_path_only()
    {
        TenantAssertion.VerifyV2(Key, Tenant, GoldenV2, Now, "POST", "/api/accounts", out var nonce).Should().BeTrue();
        nonce.Should().Be(Nonce);
        TenantAssertion.VerifyV2(Key, Tenant, GoldenV2, Now, "post", "/api/accounts", out _).Should().BeTrue("the method is case-insensitive");

        TenantAssertion.VerifyV2(Key, Tenant, GoldenV2, Now, "GET", "/api/accounts", out _).Should().BeFalse("another method");
        TenantAssertion.VerifyV2(Key, Tenant, GoldenV2, Now, "POST", "/api/transactions", out _).Should().BeFalse("another endpoint");
        TenantAssertion.VerifyV2(Key, Guid.NewGuid(), GoldenV2, Now, "POST", "/api/accounts", out _).Should().BeFalse("another tenant");
        TenantAssertion.VerifyV2("other-key", Tenant, GoldenV2, Now, "POST", "/api/accounts", out _).Should().BeFalse("another key");
    }

    [Fact]
    public void V2_expires_outside_the_validity_window_and_rejects_garbage()
    {
        TenantAssertion.VerifyV2(Key, Tenant, GoldenV2, Now.AddSeconds(59), "POST", "/api/accounts", out _).Should().BeTrue();
        TenantAssertion.VerifyV2(Key, Tenant, GoldenV2, Now.AddSeconds(61), "POST", "/api/accounts", out _).Should().BeFalse();
        TenantAssertion.VerifyV2(Key, Tenant, GoldenV2, Now.AddSeconds(-61), "POST", "/api/accounts", out _).Should().BeFalse();
        TenantAssertion.VerifyV2(Key, Tenant, "v2.nonsense", Now, "POST", "/api/accounts", out _).Should().BeFalse();
        TenantAssertion.VerifyV2(Key, Tenant, "v2.1795003200.short.abcd", Now, "POST", "/api/accounts", out _).Should().BeFalse();
        TenantAssertion.VerifyV2(Key, Tenant, null, Now, "POST", "/api/accounts", out _).Should().BeFalse();
    }

    [Fact]
    public void Check_consumes_the_nonce_so_a_replay_fails()
    {
        var cache = new TenantNonceCache(new MutableClock(Now));

        TenantAssertion.Check(Key, Tenant, GoldenV2, Now, "POST", "/api/accounts", acceptLegacy: true, cache).Should().BeTrue();
        TenantAssertion.Check(Key, Tenant, GoldenV2, Now, "POST", "/api/accounts", acceptLegacy: true, cache).Should().BeFalse("same header again is a replay");
    }

    [Fact]
    public void Check_without_a_cache_is_a_pure_verification()
    {
        TenantAssertion.Check(Key, Tenant, GoldenV2, Now, "POST", "/api/accounts", acceptLegacy: true, nonces: null).Should().BeTrue();
        TenantAssertion.Check(Key, Tenant, GoldenV2, Now, "POST", "/api/accounts", acceptLegacy: true, nonces: null).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Legacy_v1_is_accepted_only_while_the_flag_is_on(bool acceptLegacy, bool expected)
        => TenantAssertion.Check(Key, Tenant, GoldenV1, Now, "GET", "/api/accounts", acceptLegacy, new TenantNonceCache(new MutableClock(Now))).Should().Be(expected);

    [Fact]
    public void Nonce_cache_forgets_a_nonce_after_the_ttl_and_is_bounded()
    {
        var clock = new MutableClock(Now);
        var cache = new TenantNonceCache(clock, maxEntries: 2);

        cache.TryRegister(Tenant, "aaaaaaaa").Should().BeTrue();
        cache.TryRegister(Tenant, "aaaaaaaa").Should().BeFalse();
        cache.TryRegister(Tenant, "bbbbbbbb").Should().BeTrue();
        cache.TryRegister(Tenant, "cccccccc").Should().BeFalse("full cache fails closed");
        cache.Count.Should().Be(2);

        clock.Advance(TenantNonceCache.Ttl + TimeSpan.FromSeconds(1));

        cache.TryRegister(Tenant, "cccccccc").Should().BeTrue("expired entries are purged to make room");
        cache.TryRegister(Tenant, "aaaaaaaa").Should().BeTrue("an expired nonce is gone (its signature has expired too)");
    }

    [Fact]
    public void Nonce_is_scoped_to_the_tenant()
    {
        var cache = new TenantNonceCache(new MutableClock(Now));

        cache.TryRegister(Tenant, "aaaaaaaa").Should().BeTrue();
        cache.TryRegister(Guid.NewGuid(), "aaaaaaaa").Should().BeTrue();
    }

    // ---- through HeaderTenantContext ----------------------------------------------------------------------------------------------

    private static HeaderTenantContext Context(
        string? sig, bool acceptLegacy, TenantNonceCache cache, TimeProvider clock, string method = "POST", string path = "/api/accounts", HttpContext? http = null)
    {
        http ??= new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.Headers["X-Tenant-Id"] = Tenant.ToString();
        if (sig is not null)
        {
            http.Request.Headers["X-Tenant-Sig"] = sig;
        }

        var options = Options.Create(new ApiOptions { Key = "k", TenantSigningKey = Key, EffectiveRequireSignedTenant = true, EffectiveAcceptLegacyTenantSignature = acceptLegacy });
        return new HeaderTenantContext(new HttpContextAccessor { HttpContext = http }, options, new TenantOverride(), cache, clock);
    }

    [Fact]
    public void Context_accepts_v2_once_then_rejects_the_replay_and_a_different_path()
    {
        var clock = new MutableClock(Now);
        var cache = new TenantNonceCache(clock);

        Context(GoldenV2, acceptLegacy: false, cache, clock).TenantId.Should().Be(Tenant);

        var replay = () => Context(GoldenV2, acceptLegacy: false, cache, clock);
        replay.Should().Throw<TenantResolutionException>();

        var otherPath = () => Context(TenantAssertion.SignV2(Key, Tenant, Now, "POST", "/api/accounts", "ffffffffffff"), false, cache, clock, path: "/api/transactions");
        otherPath.Should().Throw<TenantResolutionException>();
    }

    [Fact]
    public void A_second_context_in_the_same_request_does_not_trip_the_replay_check()
    {
        var clock = new MutableClock(Now);
        var cache = new TenantNonceCache(clock);
        var http = new DefaultHttpContext();

        Context(GoldenV2, acceptLegacy: false, cache, clock, http: http).TenantId.Should().Be(Tenant);
        Context(GoldenV2, acceptLegacy: false, cache, clock, http: http).TenantId.Should().Be(Tenant);
    }

    [Fact]
    public void Context_honours_the_legacy_flag_for_v1_headers()
    {
        var clock = new MutableClock(Now);

        Context(GoldenV1, acceptLegacy: true, new TenantNonceCache(clock), clock).TenantId.Should().Be(Tenant);

        var rejected = () => Context(GoldenV1, acceptLegacy: false, new TenantNonceCache(clock), clock);
        rejected.Should().Throw<TenantResolutionException>();
    }
}
