using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.Web.ApiClient;

/// <summary>Signing side of the tenant assertion; the API verifies it (<c>FlowLedger.Api.Tenancy.TenantAssertion</c>). Formats must match.</summary>
public static class TenantAssertionSigner
{
    public static string Sign(string key, Guid tenantId, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds();
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{tenantId:N}.{ts}"))).ToLowerInvariant();
        return $"{ts}.{mac}";
    }
}
