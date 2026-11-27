using System.Text;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>REST calls.</summary>
public sealed partial class IntegrationService
{
    // ── REST ─────────────────────────────────────────────────────────────────────

    public async Task<(IntegrationCallResult? Result, CallRejection? Error)> CallRestAsync(Guid id, RestCallRequest r, CancellationToken ct = default)
    {
        var integration = await store.GetAsync(TenantId, id, ct);
        if (integration is null || integration.Kind != IntegrationKind.Rest)
        {
            return (null, new CallRejection("That REST integration doesn't exist."));
        }

        if (!integration.Enabled)
        {
            return (null, new CallRejection("That integration is turned off."));
        }

        var method = (r.Method ?? "GET").Trim().ToUpperInvariant();
        if (!RestMethods.Contains(method))
        {
            return (null, new CallRejection("Use GET, POST, PUT, PATCH, DELETE or HEAD.", "method"));
        }

        if (!TryBuildRestUrl(integration.Config.Url, r.Path, r.Query, out var url, out var urlError, out var urlField))
        {
            return (null, new CallRejection(urlError!, urlField));
        }

        if (!OutboundHeaders.TryClean(r.Headers, out var extra, out var headerError))
        {
            return (null, new CallRejection(headerError!, "headers"));
        }

        if (r.Body is { } body && Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
        {
            return (null, new CallRejection("The request body is too large (256 KB maximum).", "body"));
        }

        var secrets = await store.GetSecretsAsync(TenantId, id, ct);
        var headers = OutboundHeaders.Build(integration.Config, secrets, extra);
        var hasBody = !string.IsNullOrEmpty(r.Body) && method is not ("GET" or "HEAD");
        var result = await SendAndLogAsync(
            integration, "call", new TransportRequest(method, url!, headers, hasBody ? r.Body : null, hasBody ? (r.ContentType ?? "application/json") : null), ct);
        return (result, null);
    }


    private bool TryBuildRestUrl(string baseUrl, string? path, string? query, out string? url, out string? error, out string? field)
    {
        url = null;
        error = null;
        field = "path";
        var p = (path ?? "").Trim();
        var q = (query ?? "").Trim().TrimStart('?');
        if (p.Contains("://") || p.StartsWith("//", StringComparison.Ordinal) || p.Contains('\\') || p.Contains('#') || p.Contains(".."))
        {
            error = "Use a path under the integration's base URL (like /v1/items), not a full URL.";
            return false;
        }

        if (q.Contains('#') || q.Length > 1024)
        {
            error = "The query string is invalid.";
            field = "query";
            return false;
        }

        if (p.Length > 0 && !p.StartsWith('/'))
        {
            p = "/" + p;
        }

        var baseUri = new Uri(baseUrl);
        var combined = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + p;
        var baseQuery = baseUri.Query.TrimStart('?');
        var fullQuery = string.Join('&', new[] { baseQuery, q }.Where(s => s.Length > 0));
        var candidate = fullQuery.Length > 0 ? combined + "?" + fullQuery : combined;
        if (!IntegrationUrl.TryValidate(candidate, AllowLocal, out url, out error))
        {
            return false;
        }

        // The path must not escape the base host (a "@" in the path can't, but be explicit).
        if (!new Uri(url!).Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            url = null;
            error = "The request must stay on the integration's host.";
            return false;
        }

        return true;
    }
}
