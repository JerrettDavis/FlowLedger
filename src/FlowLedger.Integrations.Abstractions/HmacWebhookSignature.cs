using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// The one place webhook HMAC-SHA256 signatures are computed and compared (#100). Connectors differ only in what they sign
/// (body, or timestamp + body) and how the digest is encoded (hex or Base64); the keyed hash and the single constant-time
/// comparison live here so no verifier hand-rolls its own.
/// </summary>
public static class HmacWebhookSignature
{
    /// <summary>Lowercase-hex HMAC-SHA256 of <paramref name="payload"/>.</summary>
    public static string ComputeHex(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(HMACSHA256.HashData(key, payload)).ToLowerInvariant();

    /// <summary>Base64 HMAC-SHA256 of <paramref name="payload"/>.</summary>
    public static string ComputeBase64(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, payload));

    /// <summary>
    /// Constant-time comparison of the signature the sender presented against the one we computed. A missing or wrong-length
    /// presented value is simply a mismatch (the length of a public digest is not a secret).
    /// </summary>
    public static bool Matches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == presentedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }
}
