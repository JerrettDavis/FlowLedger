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
internal sealed class ApiAuthHeaderHandler(IOptions<ApiAuthOptions> options, FlowLedger.Web.Auth.OidcOptions oidc) : DelegatingHandler
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

        // Single-household mode (no OIDC): the configured tenant is authoritative. With OIDC the signed-in user's own
        // tenant is set per client (see FlowLedgerApiClient) and a missing one must fail closed, never default.
        if (!oidc.Enabled && !string.IsNullOrEmpty(_options.TenantId))
        {
            request.Headers.Remove("X-Tenant-Id");
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", _options.TenantId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
