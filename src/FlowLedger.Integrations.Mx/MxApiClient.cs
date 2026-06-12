using System.Net.Http.Json;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Contracts;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Thin typed wrapper over an injected <see cref="HttpClient"/> — the ONLY class that knows
/// MX wire shapes and endpoints. Each public method is one MX endpoint. All HTTP failures are
/// funnelled through <see cref="MxErrorMapper"/> into the provider exception taxonomy.
///
/// The <see cref="HttpClient"/> is configured by <c>AddMxProvider</c> with BaseAddress,
/// HTTP Basic auth (ClientId:ApiKey), the MX vendor Accept header, and a standard
/// resilience handler (Microsoft.Extensions.Http.Resilience).
/// </summary>
internal sealed class MxApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<MxApiClient> _logger;

    public MxApiClient(HttpClient http, ILogger<MxApiClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Users ──────────────────────────────────────────────────────────────────

    /// <summary>POST /users — creates an MX user and returns its guid.</summary>
    public async Task<MxUser> CreateUserAsync(string externalId, CancellationToken ct)
    {
        var request = new MxUserRequest(new MxUserBody(externalId, Metadata: null));
        var response = await SendAsync(
            HttpMethod.Post, "/users", request, MxJsonContext.Default.MxUserRequest, "CreateUser", ct)
            .ConfigureAwait(false);

        var body = await ReadAsync(response, MxJsonContext.Default.MxUserResponse, "CreateUser", ct)
            .ConfigureAwait(false);

        return body.User ?? throw new FatalProviderException("MX CreateUser returned no user.");
    }

    // ── Members ────────────────────────────────────────────────────────────────

    /// <summary>POST /users/{userGuid}/members — creates a member for the given institution.</summary>
    public async Task<MxMember> CreateMemberAsync(string userGuid, string institutionCode, CancellationToken ct)
    {
        var request = new MxMemberRequest(new MxMemberBody(institutionCode));
        var response = await SendAsync(
            HttpMethod.Post, $"/users/{userGuid}/members", request,
            MxJsonContext.Default.MxMemberRequest, "CreateMember", ct)
            .ConfigureAwait(false);

        var body = await ReadAsync(response, MxJsonContext.Default.MxMemberResponse, "CreateMember", ct)
            .ConfigureAwait(false);

        return body.Member ?? throw new FatalProviderException("MX CreateMember returned no member.");
    }

    /// <summary>GET /users/{userGuid}/members/{memberGuid}/status — current connection status.</summary>
    public async Task<MxMember> GetMemberStatusAsync(string userGuid, string memberGuid, CancellationToken ct)
    {
        var response = await SendAsync(
            HttpMethod.Get, $"/users/{userGuid}/members/{memberGuid}/status",
            request: (object?)null, jsonTypeInfo: null, "GetMemberStatus", ct)
            .ConfigureAwait(false);

        var body = await ReadAsync(response, MxJsonContext.Default.MxMemberResponse, "GetMemberStatus", ct)
            .ConfigureAwait(false);

        return body.Member ?? throw new FatalProviderException("MX GetMemberStatus returned no member.");
    }

    // ── Connect widget ───────────────────────────────────────────────────────────

    /// <summary>POST /users/{userGuid}/widget_urls — obtains a Connect widget URL.</summary>
    public async Task<MxWidget> GetConnectWidgetUrlAsync(string userGuid, CancellationToken ct)
    {
        var request = new MxWidgetRequest(new MxWidgetBody("connect_widget", "aggregation"));
        var response = await SendAsync(
            HttpMethod.Post, $"/users/{userGuid}/widget_urls", request,
            MxJsonContext.Default.MxWidgetRequest, "GetConnectWidgetUrl", ct)
            .ConfigureAwait(false);

        var body = await ReadAsync(response, MxJsonContext.Default.MxWidgetResponse, "GetConnectWidgetUrl", ct)
            .ConfigureAwait(false);

        return body.WidgetUrl ?? throw new FatalProviderException("MX GetConnectWidgetUrl returned no widget.");
    }

    // ── Accounts ──────────────────────────────────────────────────────────────

    /// <summary>GET /users/{userGuid}/members/{memberGuid}/accounts — all accounts for the member.</summary>
    public async Task<MxAccountsResponse> GetAccountsAsync(
        string userGuid, string memberGuid, int page, int recordsPerPage, CancellationToken ct)
    {
        var path = $"/users/{userGuid}/members/{memberGuid}/accounts?page={page}&records_per_page={recordsPerPage}";
        var response = await SendAsync(
            HttpMethod.Get, path, request: (object?)null, jsonTypeInfo: null, "GetAccounts", ct)
            .ConfigureAwait(false);

        return await ReadAsync(response, MxJsonContext.Default.MxAccountsResponse, "GetAccounts", ct)
            .ConfigureAwait(false);
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    /// <summary>
    /// GET /accounts/{accountGuid}/transactions — one page of transactions, paged via
    /// page / records_per_page.
    /// </summary>
    public async Task<MxTransactionsResponse> GetTransactionsAsync(
        string userGuid, string accountGuid, int page, int recordsPerPage, CancellationToken ct)
    {
        var path =
            $"/users/{userGuid}/accounts/{accountGuid}/transactions?page={page}&records_per_page={recordsPerPage}";
        var response = await SendAsync(
            HttpMethod.Get, path, request: (object?)null, jsonTypeInfo: null, "GetTransactions", ct)
            .ConfigureAwait(false);

        return await ReadAsync(response, MxJsonContext.Default.MxTransactionsResponse, "GetTransactions", ct)
            .ConfigureAwait(false);
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest? request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest>? jsonTypeInfo,
        string operation,
        CancellationToken ct)
        where TRequest : class
    {
        using var message = new HttpRequestMessage(method, path);
        if (request is not null && jsonTypeInfo is not null)
        {
            message.Content = JsonContent.Create(request, jsonTypeInfo);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw MxErrorMapper.FromNetworkFailure(operation, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout (not caller cancellation) — treat as transient.
            throw MxErrorMapper.FromNetworkFailure(operation, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var ex = MxErrorMapper.FromResponse(response, operation);
            response.Dispose();
            _logger.LogWarning(
                "MX '{Operation}' failed: {ExceptionType} ({ProviderCode}).",
                operation, ex.GetType().Name, ex.ProviderCode);
            throw ex;
        }

        return response;
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        string operation,
        CancellationToken ct)
        where T : class
    {
        try
        {
            var value = await response.Content
                .ReadFromJsonAsync(jsonTypeInfo, ct)
                .ConfigureAwait(false);

            return value ?? throw new FatalProviderException($"MX '{operation}' returned an empty body.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FatalProviderException($"MX '{operation}' returned malformed JSON.", inner: ex);
        }
        finally
        {
            response.Dispose();
        }
    }
}
