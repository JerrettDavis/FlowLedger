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
public sealed class FlowLedgerApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<FlowLedgerApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FlowLedgerApiClient(HttpClient http, ILogger<FlowLedgerApiClient> logger)
    {
        _http = http;
        _logger = logger;
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
            _logger.LogError(ex, "HTTP error during {Operation}", operationDescription);
            throw new ApiClientException(
                $"Couldn't {operationDescription} (a network or server error occurred).",
                ex.StatusCode,
                ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialization error during {Operation}", operationDescription);
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
                _logger.LogError(
                    "Non-success status {StatusCode} from {Url} during {Operation}",
                    (int)response.StatusCode, url, operationDescription);
                throw new ApiClientException(
                    $"Couldn't {operationDescription} (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        });
    }

    // ── Accounts ─────────────────────────────────────────────────────────────

    public async Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<AccountDto>>(
            "/api/accounts", "load accounts", ct: ct);
        return result ?? [];
    }

    public async Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken ct = default)
        => await GetJsonAsync<AccountDto>(
            $"/api/accounts/{id}", "load account", nullOn404: true, ct: ct);

    public async Task<AccountDto?> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("create account", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/accounts", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} creating account", (int)response.StatusCode);
                throw new ApiClientException(
                    $"Couldn't create account (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
    }

    public async Task<AccountDto?> UpdateAccountAsync(Guid id, UpdateAccountRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("update account", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/accounts/{id}", request, JsonOptions, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} updating account {Id}", (int)response.StatusCode, id);
                throw new ApiClientException(
                    $"Couldn't update account (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<List<TransactionDto>> GetTransactionsAsync(
        Guid? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (accountId.HasValue)
        {
            qs.Add($"accountId={accountId}");
        }

        if (from.HasValue)
        {
            qs.Add($"from={from:yyyy-MM-dd}");
        }

        if (to.HasValue)
        {
            qs.Add($"to={to:yyyy-MM-dd}");
        }

        if (skip > 0)
        {
            qs.Add($"skip={skip}");
        }

        if (take != 100)
        {
            qs.Add($"take={take}");
        }

        var url = "/api/transactions" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        var result = await GetJsonAsync<List<TransactionDto>>(url, "load transactions", ct: ct);
        return result ?? [];
    }

    public async Task<TransactionDto?> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("create transaction", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/transactions", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} creating transaction", (int)response.StatusCode);
                throw new ApiClientException(
                    $"Couldn't create transaction (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<TransactionDto>(JsonOptions, ct);
        });
    }

    // ── Categories ────────────────────────────────────────────────────────────

    public async Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<CategoryDto>>(
            "/api/categories", "load categories", ct: ct);
        return result ?? [];
    }

    // ── Recurring Flows ───────────────────────────────────────────────────────

    public async Task<List<RecurringFlowDto>> GetRecurringFlowsAsync(CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<RecurringFlowDto>>(
            "/api/recurring-flows", "load recurring flows", ct: ct);
        return result ?? [];
    }

    public async Task<RecurringFlowDto?> CreateRecurringFlowAsync(CreateRecurringFlowRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("create recurring flow", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/recurring-flows", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} creating recurring flow", (int)response.StatusCode);
                throw new ApiClientException(
                    $"Couldn't create recurring flow (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<RecurringFlowDto>(JsonOptions, ct);
        });
    }

    public async Task<RecurringFlowDto?> UpdateRecurringFlowAsync(Guid id, UpdateRecurringFlowRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("update recurring flow", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/recurring-flows/{id}", request, JsonOptions, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} updating recurring flow {Id}", (int)response.StatusCode, id);
                throw new ApiClientException(
                    $"Couldn't update recurring flow (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<RecurringFlowDto>(JsonOptions, ct);
        });
    }

    public async Task DeleteRecurringFlowAsync(Guid id, CancellationToken ct = default)
    {
        await ExecuteAsync("delete recurring flow", async () =>
        {
            var response = await _http.DeleteAsync($"/api/recurring-flows/{id}", ct);
            if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} deleting recurring flow {Id}", (int)response.StatusCode, id);
                throw new ApiClientException(
                    $"Couldn't delete recurring flow (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return true; // dummy return to satisfy Func<Task<T>>
        });
    }

    // ── Forecast ──────────────────────────────────────────────────────────────

    public async Task<ForecastResultDto?> GetForecastAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        int? months = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (from.HasValue)
        {
            qs.Add($"from={from:yyyy-MM-dd}");
        }

        if (to.HasValue)
        {
            qs.Add($"to={to:yyyy-MM-dd}");
        }

        if (months.HasValue)
        {
            qs.Add($"months={months}");
        }

        var url = "/api/forecast" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        // 404 = no forecast data yet (no accounts synced) — return null to signal "no data"
        return await GetJsonAsync<ForecastResultDto>(url, "load forecast data", nullOn404: true, ct: ct);
    }

    // ── Connect / Sync ────────────────────────────────────────────────────────

    public async Task<ConnectResult?> ConnectAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("connect provider", async () =>
        {
            var response = await _http.PostAsync("/api/connect", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} connecting provider", (int)response.StatusCode);
                throw new ApiClientException(
                    $"Couldn't connect provider (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<ConnectResult>(JsonOptions, ct);
        });
    }

    public async Task<SyncResult?> SyncAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("sync data", async () =>
        {
            var response = await _http.PostAsync("/api/sync", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} syncing data", (int)response.StatusCode);
                throw new ApiClientException(
                    $"Couldn't sync data (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<SyncResult>(JsonOptions, ct);
        });
    }
}
