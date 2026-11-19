using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Verifies the <c>X-Tenant-Sig</c> header the Web app attaches to every request: <c>{unixSeconds}.{hex HMAC-SHA256}</c> over
/// <c>{tenantId:N}.{unixSeconds}</c> with <c>Api:TenantSigningKey</c>. The key is separate from <c>Api:Key</c>, so a leaked API key
/// alone no longer lets a caller choose which tenant to act as. The Web app implements the signing side
/// (<c>FlowLedger.Web.ApiClient.TenantAssertionSigner</c>); keep both formats identical.
/// </summary>
public static class TenantAssertion
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    public static string Sign(string key, Guid tenantId, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds();
        return $"{ts}.{Mac(key, tenantId, ts)}";
    }

    public static bool Verify(string key, Guid tenantId, string? header, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        var dot = header.IndexOf('.');
        if (dot <= 0 || !long.TryParse(header.AsSpan(0, dot), out var ts))
        {
            return false;
        }

        var age = now.ToUnixTimeSeconds() - ts;
        if (age > MaxAge.TotalSeconds || age < -MaxAge.TotalSeconds)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Mac(key, tenantId, ts));
        var actual = Encoding.ASCII.GetBytes(header[(dot + 1)..].ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Mac(string key, Guid tenantId, long ts)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{tenantId:N}.{ts}"))).ToLowerInvariant();
}
