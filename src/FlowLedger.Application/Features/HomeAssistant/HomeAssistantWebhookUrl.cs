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

    public static bool TryValidate(string? raw, out string? normalized, out string? error)
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

        if (IsLinkLocal(uri.Host))
        {
            error = "Link-local addresses are not allowed.";
            return false;
        }

        if (uri.Scheme == "http" && !IsLocalNetwork(uri.Host))
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

    private static bool IsLinkLocal(string host)
        => IPAddress.TryParse(host, out var ip)
            && (ip.IsIPv6LinkLocal || (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes() is [169, 254, ..]));

    private static bool IsLocalNetwork(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.'))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip) || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal)
        {
            return true;
        }

        return ip.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..];
    }
}
