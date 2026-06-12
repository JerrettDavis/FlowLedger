using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FlowLedger.Api.Auth;

/// <summary>
/// Validates the request API key against the configured <c>Api:Key</c> value.
/// Accepts "Authorization: Bearer {key}" or "X-Api-Key: {key}".
/// Uses fixed-time comparison to prevent timing attacks.
///
/// Returns <see cref="AuthenticateResult.NoResult"/> (not <see cref="AuthenticateResult.Fail"/>)
/// when no API key header is present, so that anonymous endpoints (health, webhook)
/// can still proceed without a 401 challenge. A wrong key returns Fail; a matching key
/// returns Success.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsSnapshot<ApiOptions> apiOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = apiOptions.Value.Key;

        // Extract the supplied key from headers.
        string? suppliedKey = null;

        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var value = authHeader.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                suppliedKey = value["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(suppliedKey)
            && Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader))
        {
            suppliedKey = apiKeyHeader.ToString().Trim();
        }

        if (string.IsNullOrEmpty(suppliedKey))
        {
            // No key presented at all. Return NoResult, not Fail, so anonymous
            // endpoints (webhook, health) pass through without a 401 challenge.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Fixed-time comparison of byte representations to prevent timing attacks.
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        if (!CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "api-client") };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
