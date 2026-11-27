using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Verifies the <c>X-Tenant-Sig</c> header the Web app attaches to every request (#135). Two wire formats exist:
/// <list type="bullet">
/// <item><b>v2</b> (current): <c>v2.{unixSeconds}.{nonce}.{hex HMAC-SHA256}</c> over <c>v2\n{tenantId:N}\n{unixSeconds}\n{nonce}\n{METHOD}\n{path}</c>
/// with <c>Api:TenantSigningKey</c>. Bound to the HTTP method and (decoded) path, and the nonce is single-use inside the validity window
/// (<see cref="TenantNonceCache"/>), so a captured header cannot be replayed or reused for another endpoint.</item>
/// <item><b>v1</b> (legacy): <c>{unixSeconds}.{hex HMAC-SHA256}</c> over <c>{tenantId:N}.{unixSeconds}</c>. Accepted only while
/// <c>Api:AcceptLegacyTenantSignature</c> is true (default true for one release so api/web version skew cannot break; upgrade the API first).</item>
/// </list>
/// The key is separate from <c>Api:Key</c>, so a leaked API key alone no longer lets a caller choose which tenant to act as. The Web app
/// implements the signing side (<c>FlowLedger.Web.ApiClient.TenantAssertionSigner</c>); keep both formats identical. The query string is not signed.
/// </summary>
public static class TenantAssertion
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    private const string V2Prefix = "v2.";

    // ---- v1 (legacy) -------------------------------------------------------------------------------------------------------------

    public static string Sign(string key, Guid tenantId, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds();
        return $"{ts}.{Mac(key, tenantId, ts)}";
    }

    /// <summary>Verifies a v1 header only.</summary>
    public static bool Verify(string key, Guid tenantId, string? header, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        var dot = header.IndexOf('.');
        if (dot <= 0 || !long.TryParse(header.AsSpan(0, dot), out var ts) || !WithinWindow(ts, now))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Mac(key, tenantId, ts));
        var actual = Encoding.ASCII.GetBytes(header[(dot + 1)..].ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    // ---- v2 ----------------------------------------------------------------------------------------------------------------------

    public static string SignV2(string key, Guid tenantId, DateTimeOffset now, string method, string path, string nonce)
    {
        var ts = now.ToUnixTimeSeconds();
        return $"{V2Prefix}{ts}.{nonce}.{MacV2(key, tenantId, ts, nonce, method, path)}";
    }

    /// <summary>Verifies the signature of a v2 header (no replay check; see <see cref="Check"/>). Returns the nonce on success.</summary>
    public static bool VerifyV2(string key, Guid tenantId, string? header, DateTimeOffset now, string method, string path, out string nonce)
    {
        nonce = string.Empty;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(header) || !header.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = header[V2Prefix.Length..].Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], out var ts) || !WithinWindow(ts, now) || parts[1].Length is < 8 or > 64)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(MacV2(key, tenantId, ts, parts[1], method, path));
        var actual = Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return false;
        }

        nonce = parts[1];
        return true;
    }

    /// <summary>
    /// Verifies a header of either format. A v2 header is checked against the request method and path and, when a <paramref name="nonces"/>
    /// cache is supplied, its nonce is consumed (a second use is a replay and fails). A v1 header passes only when <paramref name="acceptLegacy"/>.
    /// </summary>
    public static bool Check(string key, Guid tenantId, string? header, DateTimeOffset now, string method, string path, bool acceptLegacy, TenantNonceCache? nonces)
    {
        if (header is not null && header.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            return VerifyV2(key, tenantId, header, now, method, path, out var nonce) && (nonces is null || nonces.TryRegister(tenantId, nonce));
        }

        return acceptLegacy && Verify(key, tenantId, header, now);
    }

    private static bool WithinWindow(long ts, DateTimeOffset now)
    {
        var age = now.ToUnixTimeSeconds() - ts;
        return age <= MaxAge.TotalSeconds && age >= -MaxAge.TotalSeconds;
    }

    private static string Mac(string key, Guid tenantId, long ts)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{tenantId:N}.{ts}"))).ToLowerInvariant();

    private static string MacV2(string key, Guid tenantId, long ts, string nonce, string method, string path)
        => Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes($"v2\n{tenantId:N}\n{ts}\n{nonce}\n{method.ToUpperInvariant()}\n{path}"))).ToLowerInvariant();
}
