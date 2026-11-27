using Microsoft.Extensions.Options;

namespace FlowLedger.Web.ApiClient;

/// <summary>
/// Delegating handler that adds authentication headers to every outbound API request:
///   X-Api-Key: {Api:Key}        — required by ApiKeyAuthenticationHandler on the API
///   X-Tenant-Id: {Api:TenantId} — identifies the household tenant for all environments
///
/// Registered on the FlowLedgerApiClient typed HttpClient in Program.cs.
/// Values are read from the "Api" configuration section (bound to <see cref="ApiAuthOptions"/>).
/// </summary>
internal sealed class ApiAuthHeaderHandler(IOptions<ApiAuthOptions> options, FlowLedger.Web.Auth.OidcOptions oidc, IConfiguration configuration) : DelegatingHandler
{
    private readonly ApiAuthOptions _options = options.Value;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_options.Key))
        {
            // Overwrite, never append: a resilience retry re-sends the same request object, and a doubled key fails auth (401).
            request.Headers.Remove("X-Api-Key");
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.Key);
        }

        // Whole-database admin endpoints additionally require DataAdmin:Key (same value on API and Web) when the operator set one.
        if (configuration["DataAdmin:Key"] is { Length: > 0 } adminKey
            && request.RequestUri?.AbsolutePath.StartsWith("/api/data/", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Headers.Remove("X-Admin-Token");
            request.Headers.TryAddWithoutValidation("X-Admin-Token", adminKey);
        }

        // Single-household mode (no OIDC): the configured tenant is authoritative. With OIDC the signed-in user's own
        // tenant is set per client (see FlowLedgerApiClient) and a missing one must fail closed, never default.
        if (!oidc.Enabled && !string.IsNullOrEmpty(_options.TenantId))
        {
            request.Headers.Remove("X-Tenant-Id");
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", _options.TenantId);
        }

        // Vouch for the tenant this request acts as (#135 v2: bound to method + path, fresh timestamp and single-use nonce per attempt, so resilience retries stay valid).
        request.Headers.Remove("X-Tenant-Sig");
        if (!string.IsNullOrEmpty(_options.TenantSigningKey)
            && request.Headers.TryGetValues("X-Tenant-Id", out var tenantValues)
            && Guid.TryParse(tenantValues.FirstOrDefault(), out var tenantId))
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Sig", TenantAssertionSigner.SignV2(_options.TenantSigningKey, tenantId, DateTimeOffset.UtcNow, request.Method.Method, TenantAssertionSigner.PathOf(request.RequestUri)));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
