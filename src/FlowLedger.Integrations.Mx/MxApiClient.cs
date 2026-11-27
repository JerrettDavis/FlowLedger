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
internal sealed partial class MxApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<MxApiClient> _logger;
    private readonly IRawPayloadArchive _archive;

    public MxApiClient(HttpClient http, ILogger<MxApiClient> logger, IRawPayloadArchive archive)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
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

    /// <summary>
    /// GET /users — pages through users looking for one whose external <c>id</c> matches. Used to
    /// recover from a partially failed provision instead of POSTing a duplicate user.
    /// </summary>
    public async Task<MxUser?> FindUserByExternalIdAsync(string externalId, CancellationToken ct)
    {
        for (var page = 1; page <= 50; page++)
        {
            var response = await SendAsync(
                HttpMethod.Get, $"/users?page={page}&records_per_page=100", request: (object?)null,
                jsonTypeInfo: null, "ListUsers", ct).ConfigureAwait(false);
            var body = await ReadAsync(response, MxJsonContext.Default.MxUsersResponse, "ListUsers", ct)
                .ConfigureAwait(false);

            var match = body.Users?.FirstOrDefault(u => string.Equals(u.Id, externalId, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            if (body.Pagination?.TotalPages is not { } total || page >= total)
            {
                return null;
            }
        }

        return null;
    }

    private const int MemberLookupRecordsPerPage = 100;
    private const int MemberLookupMaxPages = 50; // Safety bound against a runaway pagination loop.

    /// <summary>
    /// GET /users/{userGuid}/members — lists ALL members for the user, paging until MX reports no
    /// further pages. Used to discover widget-created members; the sync path never POSTs members
    /// (MX rejects server-side member creation without credentials).
    /// </summary>
    public async Task<IReadOnlyList<MxMember>> ListMembersAsync(string userGuid, CancellationToken ct)
    {
        var result = new List<MxMember>();
        for (var page = 1; page <= MemberLookupMaxPages; page++)
        {
            var response = await SendAsync(
                HttpMethod.Get, $"/users/{userGuid}/members?page={page}&records_per_page={MemberLookupRecordsPerPage}",
                request: (object?)null, jsonTypeInfo: null, "ListMembers", ct).ConfigureAwait(false);
            var body = await ReadAsync(response, MxJsonContext.Default.MxMembersResponse, "ListMembers", ct)
                .ConfigureAwait(false);

            if (body.Members is { Count: > 0 } members)
            {
                result.AddRange(members);
            }

            // Stop when MX reports no further pages (or omits pagination → single page).
            if (body.Pagination?.TotalPages is not { } total || page >= total)
            {
                break;
            }
        }

        return result;
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
        string userGuid, string accountGuid, int page, int recordsPerPage, CancellationToken ct, string? fromDate = null)
    {
        var path =
            $"/users/{userGuid}/accounts/{accountGuid}/transactions?page={page}&records_per_page={recordsPerPage}" +
            (fromDate is null ? string.Empty : $"&from_date={fromDate}");
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
            // Keep the provider's error body too: it is the only evidence of why a billable call failed.
            // Best-effort — never mask the original failure with an archive problem.
            try
            {
                var errBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                await _archive.ArchiveAsync("MX", operation + "-Error", errBytes,
                    new RawPayloadContext(method.Method, message.RequestUri?.PathAndQuery ?? path, (int)response.StatusCode), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception archiveEx)
            {
                LogArchiveErrorBodyFailed(archiveEx, operation);
            }

            var ex = MxErrorMapper.FromResponse(response, operation);
            response.Dispose();
            LogOperationFailed(operation, ex.GetType().Name, ex.ProviderCode);
            throw ex;
        }

        return response;
    }

    private async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        string operation,
        CancellationToken ct)
        where T : class
    {
        try
        {
            // Archive the exact bytes BEFORE parsing. A failed archive write propagates and fails
            // the call: we must never consume billable provider data without retaining it.
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var req = response.RequestMessage;
            var context = new RawPayloadContext(
                req?.Method.Method ?? "GET",
                req?.RequestUri?.PathAndQuery ?? string.Empty,
                (int)response.StatusCode);
            await _archive.ArchiveAsync("MX", operation, bytes, context, ct).ConfigureAwait(false);

            var value = System.Text.Json.JsonSerializer.Deserialize(bytes, jsonTypeInfo);

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to archive MX error body for '{Operation}'.")]
    private partial void LogArchiveErrorBodyFailed(Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MX '{Operation}' failed: {ExceptionType} ({ProviderCode}).")]
    private partial void LogOperationFailed(string operation, string exceptionType, string? providerCode);
}
