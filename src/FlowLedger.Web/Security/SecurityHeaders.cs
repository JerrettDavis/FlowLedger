using System.Security.Cryptography;

namespace FlowLedger.Web.Security;

/// <summary>
/// Browser security headers for the Blazor Web App (Interactive Server, MudBlazor).
/// <list type="bullet">
/// <item>No framing (clickjacking): <c>frame-ancestors 'none'</c> + <c>X-Frame-Options: DENY</c>.</item>
/// <item><c>nosniff</c>, a strict referrer policy, and a restrictive Permissions-Policy.</item>
/// <item>CSP (outside Development: hot reload / the WASM debugger inject scripts): scripts only from self, plus
    ///   a per-request nonce for the inline
///   <c>&lt;ImportMap&gt;</c>. MudBlazor sets inline styles, so <c>style-src</c> needs <c>'unsafe-inline'</c>;
///   the Inter font is self-hosted from wwwroot/fonts. <c>form-action</c> is deliberately NOT set: the OIDC sign-out POST
///   redirects to the identity provider, which Chrome would block.</item>
/// </list>
/// </summary>
public static class SecurityHeaders
{
    private const string NonceItem = "fl-csp-nonce";

    /// <summary>The CSP nonce for this request (rendered on the inline import map in App.razor).</summary>
    public static string? Nonce(HttpContext? http) => http?.Items[NonceItem] as string;

    public static string BuildCsp(string nonce) => string.Join("; ",
        "default-src 'self'",
        $"script-src 'self' 'nonce-{nonce}'",
        "style-src 'self' 'unsafe-inline'",
        "font-src 'self' data:",
        "img-src 'self' data: blob:",
        "connect-src 'self'",
        "worker-src 'self' blob:",
        "object-src 'none'",
        "base-uri 'self'",
        "frame-ancestors 'none'");

    public static IApplicationBuilder UseFlowLedgerSecurityHeaders(this IApplicationBuilder app, bool enableCsp) =>
        app.Use(async (http, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            http.Items[NonceItem] = nonce;
            http.Response.OnStarting(() =>
            {
                var h = http.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                if (enableCsp)
                {
                    h.ContentSecurityPolicy = BuildCsp(nonce);
                }
                else
                {
                    h.ContentSecurityPolicy = "frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
                }

                return Task.CompletedTask;
            });
            await next();
        });
}
