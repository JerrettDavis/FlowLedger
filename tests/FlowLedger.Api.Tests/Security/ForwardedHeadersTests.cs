using System.Net;
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

/// <summary>Forwarded headers are honoured only from configured proxies, and X-Forwarded-Host only when AllowedHosts is restricted.</summary>
public sealed class ForwardedHeadersTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static async Task<string> SeenByAppAsync(IConfiguration cfg, string callerIp)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s => s.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Configure(o, cfg)));
            web.Configure(app =>
            {
                app.Use((ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse(callerIp);
                    return next();
                });
                app.UseForwardedHeaders();
                app.Run(ctx => ctx.Response.WriteAsync($"{ctx.Request.Scheme}|{ctx.Request.Host}|{ctx.Connection.RemoteIpAddress}"));
            });
        }).StartAsync();

        var client = host.GetTestClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "http://internal.local/");
        req.Headers.Add("X-Forwarded-Host", "evil.example");
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-For", "203.0.113.50");
        return await (await client.SendAsync(req)).Content.ReadAsStringAsync();
    }

    [Fact]
    public void Enabled_without_known_proxies_or_networks_fails_startup()
    {
        var act = () => ForwardedHeadersSetup.Validate(Config(("ForwardedHeaders:Enabled", "true")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*KnownProxies*KnownNetworks*");
    }

    [Fact]
    public void Disabled_needs_no_proxy_configuration() =>
        ForwardedHeadersSetup.Validate(Config(("ForwardedHeaders:Enabled", "false")));

    [Fact]
    public void Invalid_proxy_address_fails_startup()
    {
        var act = () => ForwardedHeadersSetup.Validate(Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "not-an-ip")));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Spoofed_forwarded_headers_from_an_unlisted_address_are_ignored()
    {
        var cfg = Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "10.0.0.5"), ("AllowedHosts", "app.example.com"));

        var seen = await SeenByAppAsync(cfg, callerIp: "198.51.100.7");

        seen.Should().Be("http|internal.local|198.51.100.7");
    }

    [Fact]
    public async Task Headers_from_a_listed_proxy_are_honoured_when_AllowedHosts_is_restricted()
    {
        var cfg = Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "10.0.0.5"), ("AllowedHosts", "evil.example"));

        var seen = await SeenByAppAsync(cfg, callerIp: "10.0.0.5");

        seen.Should().Be("https|evil.example|203.0.113.50");
    }

    [Fact]
    public async Task Forwarded_host_is_ignored_even_from_a_listed_proxy_while_AllowedHosts_is_a_wildcard()
    {
        var cfg = Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "10.0.0.5"), ("AllowedHosts", "*"));

        var seen = await SeenByAppAsync(cfg, callerIp: "10.0.0.5");

        seen.Should().Be("https|internal.local|203.0.113.50");
    }

    [Fact]
    public async Task Known_networks_accept_cidr_ranges_and_comma_separated_lists()
    {
        var cfg = Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownNetworks", "192.168.0.0/16, 172.16.0.0/12"), ("AllowedHosts", "*"));

        var seen = await SeenByAppAsync(cfg, callerIp: "172.18.0.1");

        seen.Should().StartWith("https|");
        seen.Should().EndWith("|203.0.113.50");
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("app.example.com", false)]
    [InlineData("*.example.com", false)]
    public void Wildcard_allowed_hosts_is_detected(string? value, bool expected) =>
        ForwardedHeadersSetup.AllowsAnyHost(Config(("AllowedHosts", value))).Should().Be(expected);
}
