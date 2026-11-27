using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

/// <summary>
/// Typed HTTP client for the FlowLedger API.
/// Base address is resolved via Aspire service discovery ("https+http://api").
/// The dev tenant ID header is applied by the delegating handler registered in Program.cs.
/// All HTTP / JSON errors are wrapped as <see cref="ApiClientException"/> so callers
/// receive a clean user-facing message and the original exception is preserved and logged.
/// </summary>
public sealed partial class FlowLedgerApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<FlowLedgerApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FlowLedgerApiClient(
        HttpClient http,
        ILogger<FlowLedgerApiClient> logger,
        Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider? authState = null)
    {
        _http = http;
        _logger = logger;

        // Per-user segmentation: this client instance belongs to one circuit/user, so bind that user's tenant once.
        // ServerAuthenticationStateProvider completes synchronously (state is captured when the circuit starts).
        var task = authState?.GetAuthenticationStateAsync();
        if (task is { IsCompletedSuccessfully: true }
            && task.Result.User.FindFirst(FlowLedger.Web.Auth.AuthSetup.TenantClaim)?.Value is { Length: > 0 } tenant)
        {
            _http.DefaultRequestHeaders.Remove("X-Tenant-Id");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Tenant-Id", tenant);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Executes <paramref name="action"/>, catching <see cref="HttpRequestException"/>
    /// and <see cref="JsonException"/> and rethrowing as <see cref="ApiClientException"/>
    /// after logging the original exception.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(string operationDescription, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (ApiClientException)
        {
            // Already wrapped — propagate without re-wrapping.
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogHttpError(ex, operationDescription);
            throw new ApiClientException(
                $"Couldn't {operationDescription} (a network or server error occurred).",
                ex.StatusCode,
                ex);
        }
        catch (JsonException ex)
        {
            LogJsonError(ex, operationDescription);
            throw new ApiClientException(
                $"Couldn't {operationDescription} (the server returned an unexpected response).",
                inner: ex);
        }
    }

    /// <summary>
    /// Issues a GET and deserializes to <typeparamref name="T"/>, enforcing that the
    /// response has a success status code before reading the body.
    /// Returns <c>null</c> on HTTP 404 when <paramref name="nullOn404"/> is <c>true</c>.
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(
        string url,
        string operationDescription,
        bool nullOn404 = false,
        CancellationToken ct = default)
        where T : class
    {
        return await ExecuteAsync(operationDescription, async () =>
        {
            var response = await _http.GetAsync(url, ct);

            if (nullOn404 && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Do not log the raw URL here — it may contain account identifiers
                // embedded as query parameters (e.g. ?accountId=...) that constitute
                // PII in a financial application. The operation description is sufficient
                // to identify the failed call without exposing account-scoped data.
                LogNonSuccessDuring((int)response.StatusCode, operationDescription);
                throw new ApiClientException(
                    $"Couldn't {operationDescription} (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP error during {Operation}")]
    private partial void LogHttpError(Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "JSON deserialization error during {Operation}")]
    private partial void LogJsonError(Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Non-success status {StatusCode} during {Operation}")]
    private partial void LogNonSuccessDuring(int statusCode, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Non-success status {StatusCode} updating settings of account {Id}")]
    private partial void LogNonSuccessUpdatingAccountSettings(int statusCode, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Non-success status {StatusCode} updating account {Id}")]
    private partial void LogNonSuccessUpdatingAccount(int statusCode, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Non-success status {StatusCode} changing type of account {Id}")]
    private partial void LogNonSuccessChangingAccountType(int statusCode, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Non-success status {StatusCode} creating transaction")]
    private partial void LogNonSuccessCreatingTransaction(int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Non-success status {StatusCode} updating recurring flow {Id}")]
    private partial void LogNonSuccessUpdatingRecurringFlow(int statusCode, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Non-success status {StatusCode} deleting recurring flow {Id}")]
    private partial void LogNonSuccessDeletingRecurringFlow(int statusCode, Guid id);
}
