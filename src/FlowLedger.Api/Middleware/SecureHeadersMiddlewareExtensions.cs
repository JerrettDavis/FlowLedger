namespace FlowLedger.Api.Middleware;

/// <summary>
/// Adds security response headers to all responses.
/// Must be registered early in the pipeline (before UseAuthentication / UseAuthorization)
/// so that even error responses carry the headers.
///
/// No Content-Security-Policy is set here: this is a JSON API, not a browser-rendered
/// surface, so a CSP provides no meaningful protection and can cause preflight issues.
/// </summary>
public static class SecureHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecureHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Prevent MIME-type sniffing.
            headers["X-Content-Type-Options"] = "nosniff";

            // Prevent framing (clickjacking).
            headers["X-Frame-Options"] = "DENY";

            // Don't send referrer information cross-origin.
            headers["Referrer-Policy"] = "no-referrer";

            // Disable the legacy XSS filter (modern browsers ignore it; it can cause issues).
            headers["X-XSS-Protection"] = "0";

            // Restrict feature access (minimal policy for an API).
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

            await next(context);
        });
    }
}
