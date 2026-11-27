using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.Web.ApiClient;

/// <summary>
/// Signing side of the tenant assertion; the API verifies it (<c>FlowLedger.Api.Tenancy.TenantAssertion</c>). Formats must match.
/// v2 (#135) is bound to the HTTP method and decoded path and carries a single-use nonce; <see cref="Sign"/> is the legacy v1 format.
/// </summary>
public static class TenantAssertionSigner
{
    public static string Sign(string key, Guid tenantId, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds();
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{tenantId:N}.{ts}"))).ToLowerInvariant();
        return $"{ts}.{mac}";
    }

    /// <summary>Signs a request: <c>v2.{ts}.{nonce}.{mac}</c> with a fresh random nonce.</summary>
    public static string SignV2(string key, Guid tenantId, DateTimeOffset now, string method, string path)
        => SignV2(key, tenantId, now, method, path, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());

    public static string SignV2(string key, Guid tenantId, DateTimeOffset now, string method, string path, string nonce)
    {
        var ts = now.ToUnixTimeSeconds();
        var mac = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes($"v2\n{tenantId:N}\n{ts}\n{nonce}\n{method.ToUpperInvariant()}\n{path}"))).ToLowerInvariant();
        return $"v2.{ts}.{nonce}.{mac}";
    }

    /// <summary>The path component both sides sign: percent-decoded, no query string.</summary>
    public static string PathOf(Uri? uri) => uri is { IsAbsoluteUri: true } ? Uri.UnescapeDataString(uri.AbsolutePath) : string.Empty;
}
