using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Quiltt;

/// <summary>
/// Thin wire client for Quiltt: the Session API (server key to per-profile token) and the GraphQL API (profile token to data).
/// Queries live here as constants so they are verified against the Quiltt sandbox in exactly one place.
/// </summary>
internal sealed class QuilttApiClient
{
    // `accounts` and `connections` are plain lists in the Quiltt schema (no paging args); `kind`, not `type`.
    internal const string AccountsQuery = """
        query FlowLedgerAccounts {
          accounts {
            id name kind state mask currencyCode
            balance { current available }
            institution { name }
            connection { id status }
          }
        }
        """;

    internal const string ConnectionsQuery = """
        query FlowLedgerConnections {
          connections { id status at provider }
        }
        """;

    internal const string TransactionsQuery = """
        query FlowLedgerTransactions($first: Int!, $after: String, $from: Date) {
          transactions(first: $first, after: $after, filter: { date_gte: $from }, sort: DATE_ASC) {
            nodes {
              id date description amount currencyCode status entryType
              account { id }
            }
            pageInfo { hasNextPage endCursor }
          }
        }
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly QuilttOptions _options;

    public QuilttApiClient(HttpClient http, IOptions<QuilttOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    /// <summary>Mints a profile session. With no <paramref name="profileId"/> Quiltt creates a new profile.</summary>
    public async Task<QuilttSession> CreateSessionAsync(string? profileId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKeySecret))
        {
            // Operator configuration problem, not something the end user can fix.
            throw new FatalProviderException("Quiltt:ApiKeySecret is not set.", "CONFIG") { Kind = ProviderIssueKind.Internal };
        }

        var url = new Uri(new Uri(_options.AuthBaseUrl, UriKind.Absolute), "users/sessions");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = profileId is null
                ? new StringContent("{}", Encoding.UTF8, "application/json")
                : JsonContent.Create(new { userId = profileId }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKeySecret);

        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        ThrowIfFailed(resp, QuilttIssueMapper.Endpoint.Session);

        var body = await resp.Content.ReadFromJsonAsync<QSessionResponse>(Json, ct).ConfigureAwait(false);
        var token = body?.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new FatalProviderException("Quiltt returned a session without a token.");
        }

        var id = FirstNonEmpty(body!.UserId, body.ProfileId, profileId, QuilttJwt.ProfileId(token))
                 ?? throw new FatalProviderException("Quiltt returned a session without a profile id.");
        return new QuilttSession(token, id, body.ExpiresAt ?? QuilttJwt.Expiry(token));
    }

    public async Task<IReadOnlyList<QAccount>> ListAccountsAsync(string sessionToken, CancellationToken ct)
    {
        var data = await QueryAsync<QAccountsData>(sessionToken, AccountsQuery, new { }, ct).ConfigureAwait(false);
        return data?.Accounts ?? [];
    }

    public async Task<IReadOnlyList<QConnectionNode>> ListConnectionsAsync(string sessionToken, CancellationToken ct)
    {
        var data = await QueryAsync<QConnectionsData>(sessionToken, ConnectionsQuery, new { }, ct).ConfigureAwait(false);
        return data?.Connections ?? [];
    }

    public async Task<IReadOnlyList<QTransaction>> ListTransactionsAsync(string sessionToken, DateOnly from, CancellationToken ct)
    {
        var all = new List<QTransaction>();
        string? after = null;
        for (var page = 0; page < _options.MaxPages; page++)
        {
            var data = await QueryAsync<QTransactionsData>(sessionToken, TransactionsQuery,
                new { first = _options.PageSize, after, from = from.ToString("yyyy-MM-dd") }, ct).ConfigureAwait(false);
            var conn = data?.Transactions;
            all.AddRange(conn?.Nodes ?? []);
            if (conn?.PageInfo is not { HasNextPage: true, EndCursor: { Length: > 0 } next })
            {
                return all;
            }

            after = next;
        }

        throw new FatalProviderException($"Quiltt transactions exceeded {_options.MaxPages} pages; refusing to loop.");
    }

    private async Task<T?> QueryAsync<T>(string token, string query, object variables, CancellationToken ct)
    {
        var url = new Uri(new Uri(_options.ApiBaseUrl, UriKind.Absolute), "graphql");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { query, variables }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        ThrowIfFailed(resp, QuilttIssueMapper.Endpoint.GraphQl);

        var body = await resp.Content.ReadFromJsonAsync<QGraphQlResponse<T>>(Json, ct).ConfigureAwait(false)
                   ?? throw new FatalProviderException("Quiltt returned an empty GraphQL body.");
        if (body.Errors is { Count: > 0 } errors)
        {
            throw QuilttIssueMapper.FromGraphQlErrors(errors, _options.RateLimitFallback);
        }

        return body.Data;
    }

    /// <summary>Transport failures (DNS, TLS, reset, timeout) surface as a typed transient error instead of leaking HttpRequestException.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is HttpRequestException or OperationCanceledException || ex.GetType().Name == "TimeoutRejectedException"))
        {
            throw QuilttIssueMapper.FromTransportFailure(ex);
        }
    }

    // The response body is never echoed into the message: it can carry identifiers.
    private void ThrowIfFailed(HttpResponseMessage resp, QuilttIssueMapper.Endpoint endpoint)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var retry = resp.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : null) : null;
        throw QuilttIssueMapper.FromHttpFailure(resp.StatusCode, endpoint, retry, _options.RateLimitFallback);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Reads claims from a Quiltt session JWT without validating it (the server issued it; only a fallback).</summary>
internal static class QuilttJwt
{
    public static string? ProfileId(string jwt) =>
        Claim(jwt, "profileId") ?? Claim(jwt, "pid") ?? Claim(jwt, "sub");

    public static DateTimeOffset? Expiry(string jwt) =>
        long.TryParse(Claim(jwt, "exp"), out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp) : null;

    private static string? Claim(string jwt, string name)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
