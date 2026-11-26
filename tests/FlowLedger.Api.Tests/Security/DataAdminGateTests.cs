using System.Net;
using FlowLedger.Api.Endpoints;
using FlowLedger.Api.Middleware;
using FlowLedger.ServiceDefaults;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Gate for the whole-database export/import/backup endpoints (#139): an explicit admin credential when configured, otherwise a
/// loopback check that forwarded headers cannot defeat and that a co-located reverse proxy cannot satisfy.
/// </summary>
public sealed class DataAdminGateTests
{
    private const string AdminKey = "admin-key-0123456789abcdef0123456789abcdef";

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static DefaultHttpContext Http(string? ip, string? token = null, string? xff = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        if (token is not null)
        {
            http.Request.Headers["X-Admin-Token"] = token;
        }

        if (xff is not null)
        {
            http.Request.Headers["X-Forwarded-For"] = xff;
        }

        return http;
    }

    [Fact]
    public void With_an_admin_key_configured_a_missing_or_wrong_token_is_refused_even_from_loopback()
    {
        var cfg = Config(("DataAdmin:Key", AdminKey));

        DataAdminGate.Evaluate(cfg, Http("127.0.0.1")).Allowed.Should().BeFalse();
        DataAdminGate.Evaluate(cfg, Http("127.0.0.1", token: "nope")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void With_an_admin_key_configured_the_right_token_is_accepted_from_any_address()
    {
        var cfg = Config(("DataAdmin:Key", AdminKey));

        DataAdminGate.Evaluate(cfg, Http("172.18.0.1", token: AdminKey)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void RequireLoopback_adds_the_address_condition_on_top_of_the_token()
    {
        var cfg = Config(("DataAdmin:Key", AdminKey), ("DataAdmin:RequireLoopback", "true"));

        DataAdminGate.Evaluate(cfg, Http("172.18.0.1", token: AdminKey)).Allowed.Should().BeFalse();
        DataAdminGate.Evaluate(cfg, Http("127.0.0.1", token: AdminKey)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_weak_admin_key_is_a_misconfiguration_and_refuses_everything()
    {
        var cfg = Config(("DataAdmin:Key", "short"));

        DataAdminGate.Evaluate(cfg, Http("127.0.0.1", token: "short")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Without_a_key_only_a_direct_loopback_caller_passes()
    {
        var cfg = Config();

        DataAdminGate.Evaluate(cfg, Http("127.0.0.1")).Allowed.Should().BeTrue();
        DataAdminGate.Evaluate(cfg, Http("::1")).Allowed.Should().BeTrue();
        DataAdminGate.Evaluate(cfg, Http("172.18.0.1")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_co_located_reverse_proxy_does_not_make_remote_clients_look_local()
    {
        // The proxy connects from 127.0.0.1 but always adds forwarding headers; the data endpoints must not trust that.
        DataAdminGate.Evaluate(Config(), Http("127.0.0.1", xff: "203.0.113.5")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void AllowRemote_still_opens_the_gate_for_back_compat()
    {
        DataAdminGate.Evaluate(Config(("DataAdmin:AllowRemote", "true")), Http("172.18.0.1")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Spoofed_x_forwarded_for_loopback_from_an_untrusted_peer_neither_passes_the_gate_nor_gets_a_fresh_rate_limit_bucket()
    {
        var cfg = Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "10.0.0.5"), ("AllowedHosts", "*"));
        string? gate = null, key = null, key2 = null;

        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Configure(o, cfg)));
            web.Configure(app =>
            {
                app.Use((ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7"); // not a trusted proxy
                    return next();
                });
                app.UseFlowLedgerPeerCapture();
                app.UseForwardedHeaders();
                app.Run(ctx =>
                {
                    gate = DataAdminGate.Evaluate(cfg, ctx).Allowed ? "allowed" : "denied";
                    key = RateLimitPartitions.KeyFor(ctx, signingKey: null);
                    return Task.CompletedTask;
                });
            });
        }).StartAsync();

        var client = host.GetTestClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-For", "127.0.0.1");
        await client.SendAsync(req);
        var first = key;
        var req2 = new HttpRequestMessage(HttpMethod.Get, "/");
        req2.Headers.Add("X-Forwarded-For", "192.0.2.99");
        await client.SendAsync(req2);
        key2 = key;

        gate.Should().Be("denied");
        first.Should().Be("ip:198.51.100.7");
        key2.Should().Be(first, "rotating X-Forwarded-For from an untrusted peer must not yield a fresh bucket");
    }

    [Fact]
    public async Task A_trusted_proxy_uses_only_the_address_it_appended_so_left_hand_spoofing_is_ignored()
    {
        var cfg = Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "10.0.0.5"), ("AllowedHosts", "*"));
        string? seen = null;

        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Configure(o, cfg)));
            web.Configure(app =>
            {
                app.Use((ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
                    return next();
                });
                app.UseFlowLedgerPeerCapture();
                app.UseForwardedHeaders();
                app.Run(ctx =>
                {
                    seen = ctx.Connection.RemoteIpAddress?.ToString() + "|" + DataAdminGate.Evaluate(cfg, ctx).Allowed;
                    return Task.CompletedTask;
                });
            });
        }).StartAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-For", "127.0.0.1, 203.0.113.50"); // client prepended 127.0.0.1; proxy appended the real peer
        await host.GetTestClient().SendAsync(req);

        seen.Should().Be("203.0.113.50|False");
    }
}
