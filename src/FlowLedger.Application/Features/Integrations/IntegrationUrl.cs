using FlowLedger.Application.Features.HomeAssistant;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>
/// Validates an outbound integration URL. The server makes the request, so a tenant must not be able to aim it at internal
/// services (SSRF): link-local/metadata/multicast addresses are refused for everyone, loopback/private/<c>.local</c> targets are
/// reserved for the household owner, and anything reachable over the internet must use https. The destination is checked again
/// at connect time by the transport.
/// </summary>
internal static class IntegrationUrl
{
    public const int MaxLength = 2048;

    public static bool TryValidate(string? raw, bool allowLocalNetwork, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            error = "Enter a URL.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "The URL must start with http:// or https://.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "The URL must not contain credentials or a fragment. Use the authentication fields instead.";
            return false;
        }

        var kind = HomeAssistantWebhookUrl.ClassifyHost(uri.DnsSafeHost);
        if (kind == HomeAssistantWebhookUrl.NetworkKind.Forbidden)
        {
            error = "That address is not allowed (link-local, multicast or unspecified).";
            return false;
        }

        var local = kind == HomeAssistantWebhookUrl.NetworkKind.Local;
        if (local && !allowLocalNetwork)
        {
            error = "Only the household owner can use a local-network address. Use a public https address.";
            return false;
        }

        if (uri.Scheme == "http" && !local)
        {
            error = "Use https for anything reachable over the internet (plain http is only allowed on your local network).";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path) + uri.Query;
        return true;
    }

    /// <summary>The part of a URL that is safe to log and show: scheme, host, port and path, never the query.</summary>
    public static string SafeTarget(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? Truncate(u.GetLeftPart(UriPartial.Path), 200)
            : "(invalid url)";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
