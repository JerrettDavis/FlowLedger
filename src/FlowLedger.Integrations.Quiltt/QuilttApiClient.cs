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
            throw new NeedsUserActionProviderException("Quiltt:ApiKeySecret is not set.", "Add the Quiltt API key secret to the server configuration.");
        }

        var url = new Uri(new Uri(_options.AuthBaseUrl, UriKind.Absolute), "users/sessions");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = profileId is null
                ? new StringContent("{}", Encoding.UTF8, "application/json")
                : JsonContent.Create(new { userId = profileId }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKeySecret);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        ThrowIfFailed(resp, "session");

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

    /// <summary>Quiltt's <c>accounts</c> root is a plain (unpaginated) list.</summary>
    public async Task<IReadOnlyList<QAccount>> ListAccountsAsync(string sessionToken, CancellationToken ct)
    {
        var data = await QueryAsync<QAccountsData>(sessionToken, AccountsQuery, new { }, ct).ConfigureAwait(false);
        return data?.Accounts ?? [];
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

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        ThrowIfFailed(resp, "graphql");

        var body = await resp.Content.ReadFromJsonAsync<QGraphQlResponse<T>>(Json, ct).ConfigureAwait(false)
                   ?? throw new FatalProviderException("Quiltt returned an empty GraphQL body.");
        if (body.Errors is { Count: > 0 } errors)
        {
            var text = string.Join("; ", errors.Select(e => e.Message));
            if (errors.Any(e => e.Message?.Contains("unauthor", StringComparison.OrdinalIgnoreCase) == true
                                || e.Message?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true))
            {
                throw new NeedsUserActionProviderException($"Quiltt rejected the session: {text}", "Reconnect the bank.");
            }

            throw new FatalProviderException($"Quiltt GraphQL error: {text}");
        }

        return body.Data;
    }

    // The response body is never echoed into the message: it can carry identifiers.
    private static void ThrowIfFailed(HttpResponseMessage resp, string what)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var code = (int)resp.StatusCode;
        switch (resp.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new NeedsUserActionProviderException(
                    $"Quiltt {what} request was rejected (HTTP {code}).", "Check the Quiltt API key or reconnect the bank.");
            case HttpStatusCode.TooManyRequests:
                DateTimeOffset? retry = resp.Headers.RetryAfter?.Delta is { } d ? DateTimeOffset.UtcNow + d : null;
                throw new RateLimitedProviderException("Quiltt rate limit reached.", retry);
        }

        if (code >= 500 || resp.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new TransientProviderException($"Quiltt {what} request failed (HTTP {code}).");
        }

        throw new FatalProviderException($"Quiltt {what} request failed (HTTP {code}).");
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
