using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLedger.Web.ApiClient;

/// <summary>
/// Typed HTTP client for the FlowLedger API.
/// Base address is resolved via Aspire service discovery ("https+http://api").
/// The dev tenant ID header is applied by the delegating handler registered in Program.cs.
/// </summary>
public sealed class FlowLedgerApiClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FlowLedgerApiClient(HttpClient http) => _http = http;

    // ── Accounts ─────────────────────────────────────────────────────────────

    public async Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<AccountDto>>("/api/accounts", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<AccountDto>($"/api/accounts/{id}", JsonOptions, ct);

    public async Task<AccountDto?> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/accounts", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
    }

    public async Task<AccountDto?> UpdateAccountAsync(Guid id, UpdateAccountRequest request, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/accounts/{id}", request, JsonOptions, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
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
        if (accountId.HasValue) qs.Add($"accountId={accountId}");
        if (from.HasValue) qs.Add($"from={from:yyyy-MM-dd}");
        if (to.HasValue) qs.Add($"to={to:yyyy-MM-dd}");
        if (skip > 0) qs.Add($"skip={skip}");
        if (take != 100) qs.Add($"take={take}");
        var url = "/api/transactions" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        var result = await _http.GetFromJsonAsync<List<TransactionDto>>(url, JsonOptions, ct);
        return result ?? [];
    }

    public async Task<TransactionDto?> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/transactions", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TransactionDto>(JsonOptions, ct);
    }

    // ── Categories ────────────────────────────────────────────────────────────

    public async Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<CategoryDto>>("/api/categories", JsonOptions, ct);
        return result ?? [];
    }

    // ── Recurring Flows ───────────────────────────────────────────────────────

    public async Task<List<RecurringFlowDto>> GetRecurringFlowsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<RecurringFlowDto>>("/api/recurring-flows", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<RecurringFlowDto?> CreateRecurringFlowAsync(CreateRecurringFlowRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/recurring-flows", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecurringFlowDto>(JsonOptions, ct);
    }

    public async Task<RecurringFlowDto?> UpdateRecurringFlowAsync(Guid id, UpdateRecurringFlowRequest request, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/recurring-flows/{id}", request, JsonOptions, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecurringFlowDto>(JsonOptions, ct);
    }

    public async Task DeleteRecurringFlowAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/recurring-flows/{id}", ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    // ── Forecast ──────────────────────────────────────────────────────────────

    public async Task<ForecastResultDto?> GetForecastAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        int? months = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (from.HasValue) qs.Add($"from={from:yyyy-MM-dd}");
        if (to.HasValue) qs.Add($"to={to:yyyy-MM-dd}");
        if (months.HasValue) qs.Add($"months={months}");
        var url = "/api/forecast" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        try
        {
            return await _http.GetFromJsonAsync<ForecastResultDto>(url, JsonOptions, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // ── Connect / Sync ────────────────────────────────────────────────────────

    public async Task<ConnectResult?> ConnectAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/api/connect", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConnectResult>(JsonOptions, ct);
    }

    public async Task<SyncResult?> SyncAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/api/sync", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncResult>(JsonOptions, ct);
    }
}
