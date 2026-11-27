using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.E2E.Tests;

/// <summary>
/// Direct-to-API access for seeding and isolation checks. Outside Development with OIDC on the API only honours an
/// <c>X-Tenant-Id</c> the Web app vouched for, so these calls carry an <c>X-Tenant-Sig</c> (#135 v2) signed with
/// <c>E2E_TENANT_SIGNING_KEY</c> (= the stack's <c>Api__TenantSigningKey</c>). Without that variable no signature is sent, which is
/// what an unsigned (anonymous / Development) stack expects.
/// </summary>
internal static class E2EApi
{
    internal const string DefaultApiUrl = "http://localhost:5001";
    internal const string DefaultApiKey = "dev-local-key-not-for-production";
    internal const string DefaultTenantId = "00000000-0000-0000-0000-000000000001";

    internal static string ApiUrl => (Environment.GetEnvironmentVariable("E2E_API_URL") ?? DefaultApiUrl).TrimEnd('/');

    internal static string ApiKey => Environment.GetEnvironmentVariable("E2E_API_KEY") ?? DefaultApiKey;

    /// <summary>The household tenant the signed-in browser is mapped to (Tenancy__LegacyOwnerEmail) and the stack seeds.</summary>
    internal static Guid HouseholdTenantId => Guid.Parse(Environment.GetEnvironmentVariable("E2E_TENANT_ID") ?? DefaultTenantId);

    private static string? SigningKey => Environment.GetEnvironmentVariable("E2E_TENANT_SIGNING_KEY") is { Length: > 0 } key ? key : null;

    /// <summary>An API client acting as <paramref name="tenant"/> (the household when null). Dispose it.</summary>
    internal static HttpClient CreateClient(Guid? tenant = null)
    {
        var id = tenant ?? HouseholdTenantId;
        var http = new HttpClient(new SigningHandler(id, SigningKey) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(ApiUrl + "/"),
        };
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        http.DefaultRequestHeaders.Add("X-Tenant-Id", id.ToString());
        return http;
    }

    /// <summary>Same wire format as the Web app's signer (<c>FlowLedger.Web.ApiClient.TenantAssertionSigner.SignV2</c>); keep them identical.</summary>
    internal static string SignV2(string key, Guid tenantId, DateTimeOffset now, string method, string path, string nonce)
    {
        var ts = now.ToUnixTimeSeconds();
        var mac = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes($"v2\n{tenantId:N}\n{ts}\n{nonce}\n{method.ToUpperInvariant()}\n{path}"))).ToLowerInvariant();
        return $"v2.{ts}.{nonce}.{mac}";
    }

    private sealed class SigningHandler(Guid tenantId, string? key) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (key is not null && request.RequestUri is { IsAbsoluteUri: true } uri)
            {
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                request.Headers.Remove("X-Tenant-Sig");
                request.Headers.TryAddWithoutValidation(
                    "X-Tenant-Sig",
                    SignV2(key, tenantId, DateTimeOffset.UtcNow, request.Method.Method, Uri.UnescapeDataString(uri.AbsolutePath), nonce));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
