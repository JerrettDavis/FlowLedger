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
        var a = RateLimitPartitions.KeyFor(Ctx(ApiKeyAuthenticationHandler.SchemeName, Guid.NewGuid().ToString()));
        var b = RateLimitPartitions.KeyFor(Ctx(ApiKeyAuthenticationHandler.SchemeName, Guid.NewGuid().ToString()));

        a.Should().StartWith("tenant:");
        b.Should().StartWith("tenant:");
        a.Should().NotBe(b);
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
}
