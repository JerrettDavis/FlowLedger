using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>Signing of outbound webhook deliveries and generation of signing secrets.</summary>
internal static class WebhookSigning
{
    /// <summary>
    /// <c>sha256=hex(HMAC-SHA256(secret, timestamp + "." + body))</c>. The receiver recomputes it over the raw body and rejects
    /// timestamps that are too old, which stops a captured request from being replayed.
    /// </summary>
    internal static string Sign(string secret, string timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    internal static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
