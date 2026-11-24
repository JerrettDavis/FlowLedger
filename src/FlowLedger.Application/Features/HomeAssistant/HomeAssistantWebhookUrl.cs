using System.Net;

namespace FlowLedger.Application.Features.HomeAssistant;

/// <summary>
/// Validates the Home Assistant webhook URL the household pastes in. It is a secret (anyone holding it can trigger the
/// automation), so it must be a plain absolute URL of the form <c>http(s)://host[:port]/api/webhook/&lt;id&gt;</c>:
/// no credentials, query or fragment, a long unguessable id, https for anything that is not on the local network, and never
/// a link-local (cloud metadata) address.
/// </summary>
public static class HomeAssistantWebhookUrl
{
    public const int MinIdLength = 16;
    public const int MaxLength = 512;

    /// <param name="allowLocalNetwork">True only for the household (operator) tenant: it may target Home Assistant on the LAN over plain
    /// http. Every other tenant must use https to a public host, so the server cannot be pointed at internal services (SSRF).</param>
    public static bool TryValidate(string? raw, bool allowLocalNetwork, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            error = "Enter the Home Assistant webhook URL.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "The webhook URL must start with http:// or https://.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "The webhook URL must not contain credentials, a query string or a fragment.";
            return false;
        }

        const string prefix = "/api/webhook/";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length - prefix.Length < MinIdLength || path[prefix.Length..].Contains('/'))
        {
            error = $"The URL must look like https://your-home-assistant/api/webhook/<id> with an id of at least {MinIdLength} characters.";
            return false;
        }

        var host = uri.DnsSafeHost;
        var kind = ClassifyHost(host);
        if (kind == NetworkKind.Forbidden)
        {
            error = "That address is not allowed (link-local, multicast or unspecified).";
            return false;
        }

        var local = kind == NetworkKind.Local;
        if (local && !allowLocalNetwork)
        {
            error = "Only the household owner can send to a local-network address. Use the public https address of your Home Assistant.";
            return false;
        }

        if (uri.Scheme == "http" && !local)
        {
            error = "Use https for a Home Assistant reachable over the internet (plain http is only allowed on your local network).";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path);
        return true;
    }

    /// <summary>The host (and port) to show in the UI, so the secret id is never echoed back.</summary>
    public static string? DisplayHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : null;

    public enum NetworkKind { Public, Local, Forbidden }

    /// <summary>Classifies a URL host. IP literals are classified exactly; names are judged by local-network naming (the real
    /// destination of a public-looking name is checked again at connect time, see the sender's connect guard).</summary>
    public static NetworkKind ClassifyHost(string host)
    {
        host = host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            return Classify(ip);
        }

        var h = host.TrimEnd('.');
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || !h.Contains('.')
            ? NetworkKind.Local
            : NetworkKind.Public;
    }

    /// <summary>Classifies a resolved address. IPv4-mapped IPv6 is unwrapped first so it cannot hide a blocked IPv4 range.</summary>
    public static NetworkKind Classify(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] >= 224 || b is [169, 254, ..])
            {
                return NetworkKind.Forbidden; // unspecified, multicast, reserved/broadcast, link-local (cloud metadata)
            }

            return b is [127, ..] or [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..] or [100, >= 64 and <= 127, ..]
                ? NetworkKind.Local
                : NetworkKind.Public;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any))
        {
            return NetworkKind.Forbidden;
        }

        return IPAddress.IsLoopback(ip) || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal ? NetworkKind.Local : NetworkKind.Public;
    }
}
