using System.Net;
using System.Security.Claims;
using FlowLedger.Api.Auth;
using FlowLedger.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Tests.Security;

/// <summary>Pure unit tests (no host / database): how requests are assigned to rate-limit partitions.</summary>
public sealed class RateLimitPartitionTests
{
    // KeyFor verifies signatures against the real clock, so signed assertions must be fresh: one read, shared by the tests that sign.
    private static readonly DateTimeOffset SignedAt = DateTimeOffset.UtcNow;

    private static DefaultHttpContext Ctx(string? authType, string? tenant = null, string ip = "203.0.113.7")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (authType is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "api")], authType));
        }

        if (tenant is not null)
        {
            http.Request.Headers["X-Tenant-Id"] = tenant;
        }

        return http;
    }

    [Fact]
    public void Authenticated_callers_from_the_same_ip_are_partitioned_per_tenant()
    {
        var a = RateLimitPartitions.KeyFor(Ctx(ApiKeyAuthenticationHandler.SchemeName, "00000001-0000-0000-0000-000000000000"));
        var b = RateLimitPartitions.KeyFor(Ctx(ApiKeyAuthenticationHandler.SchemeName, "00000002-0000-0000-0000-000000000000"));

        a.Should().StartWith("tenant:");
        b.Should().StartWith("tenant:");
        a.Should().NotBe(b);
    }

    [Fact]
    public void Without_a_signing_key_rotating_tenant_ids_cannot_create_more_than_a_fixed_number_of_partitions()
    {
        var keys = Enumerable.Range(0, 20_000)
            .Select(_ => RateLimitPartitions.KeyFor(Ctx(ApiKeyAuthenticationHandler.SchemeName, Guid.NewGuid().ToString())))
            .ToHashSet();

        keys.Count.Should().BeLessThanOrEqualTo(RateLimitPartitions.UnverifiedTenantBuckets);
        keys.Should().OnlyContain(k => k.StartsWith("tenant:b"));
    }

    [Fact]
    public void Same_tenant_shares_a_partition()
    {
        var t = Guid.NewGuid().ToString();
        RateLimitPartitions.KeyFor(Ctx(ApiKeyAuthenticationHandler.SchemeName, t, "10.0.0.1"))
            .Should().Be(RateLimitPartitions.KeyFor(Ctx(ApiKeyAuthenticationHandler.SchemeName, t.ToUpperInvariant(), "10.0.0.2")));
    }

    [Fact]
    public void Unauthenticated_callers_are_partitioned_by_ip_and_cannot_pick_a_tenant_partition()
    {
        var spoofed = RateLimitPartitions.KeyFor(Ctx(authType: null, tenant: Guid.NewGuid().ToString(), ip: "198.51.100.9"));
        var other = RateLimitPartitions.KeyFor(Ctx(authType: null, tenant: Guid.NewGuid().ToString(), ip: "198.51.100.9"));

        spoofed.Should().Be("ip:198.51.100.9");
        other.Should().Be(spoofed, "rotating X-Tenant-Id without a valid API key must not yield fresh budgets");
    }

    [Fact]
    public void With_a_signing_key_only_a_verified_tenant_gets_its_own_partition()
    {
        const string key = "test-signing-key-0123456789abcdef0123456789";
        var tenant = Guid.NewGuid();

        var good = Ctx(ApiKeyAuthenticationHandler.SchemeName, tenant.ToString(), "10.0.0.1");
        good.Request.Headers["X-Tenant-Sig"] = FlowLedger.Api.Tenancy.TenantAssertion.Sign(key, tenant, SignedAt);
        var forged = Ctx(ApiKeyAuthenticationHandler.SchemeName, Guid.NewGuid().ToString(), "10.0.0.1");
        forged.Request.Headers["X-Tenant-Sig"] = "1.deadbeef";

        RateLimitPartitions.KeyFor(good, key).Should().Be("tenant:" + tenant.ToString("N"));
        RateLimitPartitions.KeyFor(forged, key).Should().Be("ip:10.0.0.1", "an unverifiable tenant must not mint a partition");
    }

    [Fact]
    public void Verified_tenant_partitions_are_capped_and_overflow_into_shared_buckets()
    {
        const string key = "test-signing-key-0123456789abcdef0123456789";
        var registry = new TenantPartitionRegistry();
        var keys = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            var tenant = Guid.NewGuid();
            var http = Ctx(ApiKeyAuthenticationHandler.SchemeName, tenant.ToString(), "10.0.0.1");
            http.Request.Headers["X-Tenant-Sig"] = FlowLedger.Api.Tenancy.TenantAssertion.Sign(key, tenant, SignedAt);
            keys.Add(RateLimitPartitions.KeyFor(http, key, maxTenantPartitions: 50, registry));
        }

        registry.Count.Should().Be(50);
        keys.Count(k => k.StartsWith("tenant:o")).Should().BeLessThanOrEqualTo(RateLimitPartitions.OverflowBuckets);
        keys.Count.Should().BeLessThanOrEqualTo(50 + RateLimitPartitions.OverflowBuckets);
    }

    [Fact]
    public void Idle_partitions_are_released_so_new_tenants_are_admitted_again()
    {
        var registry = new TenantPartitionRegistry();
        var t0 = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        registry.TryAdmit("a", 1, t0).Should().BeTrue();
        registry.TryAdmit("b", 1, t0).Should().BeFalse();
        registry.TryAdmit("b", 1, t0 + TenantPartitionRegistry.IdleAfter + TimeSpan.FromSeconds(1)).Should().BeTrue();
        registry.Count.Should().Be(1);
    }

    private static HttpContext Req(string ip, string? bearer = null, bool authenticated = false, string path = "/api/accounts")
    {
        var http = Ctx(authenticated ? ApiKeyAuthenticationHandler.SchemeName : null, null, ip);
        http.Request.Path = path;
        if (bearer is not null)
        {
            http.Request.Headers["Authorization"] = "Bearer " + bearer;
        }

        return http;
    }

    [Fact]
    public async Task Global_limiter_caps_one_ip_no_matter_how_many_tenant_ids_it_rotates()
    {
        using var limiter = RateLimitPartitions.CreateGlobalLimiter(perIpPerMinute: 10, failedAuthPerIpPerMinute: 1000);

        var granted = 0;
        for (var i = 0; i < 50; i++)
        {
            var http = Req("198.51.100.1", "valid", authenticated: true);
            http.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
            using var lease = await limiter.AcquireAsync(http);
            granted += lease.IsAcquired ? 1 : 0;
        }

        granted.Should().Be(10);
        using var other = await limiter.AcquireAsync(Req("198.51.100.2", "valid", authenticated: true));
        other.IsAcquired.Should().BeTrue("another IP has its own budget");
    }

    [Fact]
    public async Task Failed_authentication_is_throttled_per_ip_after_n_failures_and_valid_callers_are_unaffected()
    {
        using var limiter = RateLimitPartitions.CreateGlobalLimiter(perIpPerMinute: 1000, failedAuthPerIpPerMinute: 5);

        var rejected = 0;
        for (var i = 0; i < 10; i++)
        {
            using var lease = await limiter.AcquireAsync(Req("203.0.113.9", "wrong-" + i));
            rejected += lease.IsAcquired ? 0 : 1;
        }

        rejected.Should().Be(5);
        using var good = await limiter.AcquireAsync(Req("203.0.113.9", "right", authenticated: true));
        good.IsAcquired.Should().BeTrue("a request that authenticated does not count against the failure budget");
    }

    [Fact]
    public async Task Health_endpoints_are_exempt_from_the_global_limiter()
    {
        using var limiter = RateLimitPartitions.CreateGlobalLimiter(perIpPerMinute: 1, failedAuthPerIpPerMinute: 1);

        for (var i = 0; i < 5; i++)
        {
            using var lease = await limiter.AcquireAsync(Req("192.0.2.1", path: "/health"));
            lease.IsAcquired.Should().BeTrue();
        }
    }

    [Fact]
    public void Webhook_partitions_are_per_provider_and_ip()
    {
        RateLimitPartitions.WebhookKey(Req("192.0.2.1", path: "/api/integrations/mx/webhooks")).Should().Be("webhook:mx:192.0.2.1");
        RateLimitPartitions.WebhookKey(Req("192.0.2.1", path: "/api/integrations/quiltt/webhooks")).Should().Be("webhook:quiltt:192.0.2.1");
    }

    [Fact]
    public void Retry_after_seconds_comes_from_the_lease_or_defaults_to_the_window()
    {
        RateLimitPartitions.RetryAfterSeconds(TimeSpan.FromSeconds(12.2)).Should().Be(13);
        RateLimitPartitions.RetryAfterSeconds(null).Should().Be(60);
    }
}
