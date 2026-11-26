using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowLedger.ServiceDefaults;

/// <summary>
/// Opt-in, trust-scoped forwarded-headers handling shared by Web and Api. Behind a reverse proxy the app would otherwise see the
/// proxy address and http scheme (breaking OIDC redirect URIs, rate-limit partitions and the loopback guard). Trusting those headers
/// from anyone is a spoofing hole, so enabling requires naming the proxies:
/// <c>ForwardedHeaders:Enabled=true</c> plus <c>ForwardedHeaders:KnownProxies</c> (IPs) and/or <c>ForwardedHeaders:KnownNetworks</c>
/// (CIDRs), as arrays or comma-separated lists. <c>X-Forwarded-Host</c> is honoured only when <c>AllowedHosts</c> is restricted.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string Section = "ForwardedHeaders";

    public static bool IsEnabled(IConfiguration config) => config.GetValue($"{Section}:Enabled", false);

    /// <summary>True when AllowedHosts is unset or a bare "*" (Host header not restricted).</summary>
    public static bool AllowsAnyHost(IConfiguration config) => AllowedHosts(config).Any(h => h == "*") || AllowedHosts(config).Count == 0;

    private static List<string> AllowedHosts(IConfiguration config) =>
        (config["AllowedHosts"] ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> List(IConfiguration config, string key)
    {
        var section = config.GetSection($"{Section}:{key}");
        var values = new List<string>();
        if (section.Value is { Length: > 0 } scalar)
        {
            values.AddRange(scalar.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        values.AddRange(section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        return values;
    }

    /// <summary>Throws when forwarded headers are enabled without at least one valid known proxy or network.</summary>
    public static void Validate(IConfiguration config)
    {
        if (!IsEnabled(config))
        {
            return;
        }

        var proxies = List(config, "KnownProxies");
        var networks = List(config, "KnownNetworks");
        if (proxies.Count == 0 && networks.Count == 0)
        {
            throw new InvalidOperationException(
                "ForwardedHeaders:Enabled=true requires ForwardedHeaders:KnownProxies and/or ForwardedHeaders:KnownNetworks " +
                "(the reverse proxy's address or CIDR as the app sees it, e.g. 172.18.0.0/16 for the compose bridge). " +
                "Trusting forwarded headers from any caller would let clients spoof scheme, host and IP.");
        }

        foreach (var p in proxies)
        {
            if (!IPAddress.TryParse(p, out _))
            {
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains '{p}', which is not an IP address.");
            }
        }

        foreach (var n in networks)
        {
            if (!System.Net.IPNetwork.TryParse(n, out _))
            {
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains '{n}', which is not a CIDR range.");
            }
        }
    }

    /// <summary>Applies the trusted proxies/networks to <paramref name="options"/>. Call <see cref="Validate"/> first.</summary>
    public static void Configure(ForwardedHeadersOptions options, IConfiguration config)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        if (!AllowsAnyHost(config))
        {
            // Host is only taken from the proxy when the app itself restricts which hosts it answers to.
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
            options.AllowedHosts = AllowedHosts(config);
        }

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var p in List(config, "KnownProxies"))
        {
            options.KnownProxies.Add(IPAddress.Parse(p));
        }

        foreach (var n in List(config, "KnownNetworks"))
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
        }
    }

    /// <summary>Validates and registers forwarded-headers options. Returns whether the middleware should be added.</summary>
    public static bool AddFlowLedgerForwardedHeaders(this WebApplicationBuilder builder)
    {
        Validate(builder.Configuration);
        if (!IsEnabled(builder.Configuration))
        {
            return false;
        }

        builder.Services.Configure<ForwardedHeadersOptions>(o => Configure(o, builder.Configuration));
        return true;
    }

    /// <summary>Adds the middleware when enabled, and warns outside Development when the Host header is not restricted.</summary>
    public static void UseFlowLedgerForwardedHeaders(this WebApplication app, bool enabled)
    {
        if (!app.Environment.IsDevelopment() && AllowsAnyHost(app.Configuration))
        {
            app.Logger.LogWarning(
                "SECURITY: AllowedHosts is '*' outside Development, so the Host header is not restricted. " +
                "Set AllowedHosts to your public host name(s) (e.g. AllowedHosts=flowledger.example.com;localhost).");
        }

        if (enabled)
        {
            app.UseForwardedHeaders();
        }
    }
}
