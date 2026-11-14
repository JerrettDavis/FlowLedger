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
                // Do not log the raw URL here — it may contain account identifiers
                // embedded as query parameters (e.g. ?accountId=...) that constitute
                // PII in a financial application. The operation description is sufficient
                // to identify the failed call without exposing account-scoped data.
                _logger.LogError(
                    "Non-success status {StatusCode} during {Operation}",
                    (int)response.StatusCode, operationDescription);
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

    // Bill sheet ---------------------------------------------------------------

    public Task<SheetViewDto?> GetSheetAsync(string? month = null, int months = 6, CancellationToken ct = default)
        => GetJsonAsync<SheetViewDto>(
            "/api/sheet?months=" + months + (string.IsNullOrWhiteSpace(month) ? "" : "&month=" + Uri.EscapeDataString(month)),
            "load the sheet", ct: ct);

    public Task<SheetRefreshResult?> RefreshSheetAsync(CancellationToken ct = default)
        => ExecuteAsync("refresh the sheet", async () =>
        {
            var response = await _http.PostAsync("/api/sheet/refresh", null, ct);
            await ThrowIfFailedAsync(response, "refresh the sheet", ct);
            return await response.Content.ReadFromJsonAsync<SheetRefreshResult>(JsonOptions, ct);
        });

    public Task SaveSheetBillAsync(Guid? id, SaveBillRequest request, CancellationToken ct = default)
        => SendNoContentAsync("save the row", () => id is null
            ? _http.PostAsJsonAsync("/api/sheet/bills", request, JsonOptions, ct)
            : _http.PutAsJsonAsync($"/api/sheet/bills/{id}", request, JsonOptions, ct), ct);

    public Task DeleteSheetBillAsync(Guid id, CancellationToken ct = default)
        => SendNoContentAsync("delete the row", () => _http.DeleteAsync($"/api/sheet/bills/{id}", ct), ct);

    public Task SetSheetPaymentAsync(Guid billId, string month, bool paid, decimal? amount = null, CancellationToken ct = default)
        => SendNoContentAsync("update the cell", () => _http.PutAsJsonAsync(
            $"/api/sheet/bills/{billId}/payments/{month}", new SetPaymentRequest(paid, amount), JsonOptions, ct), ct);

    public Task ClearSheetPaymentAsync(Guid billId, string month, CancellationToken ct = default)
        => SendNoContentAsync("reset the cell", () => _http.DeleteAsync($"/api/sheet/bills/{billId}/payments/{month}", ct), ct);

    public Task<SheetImportResult?> ImportSheetAsync(string csv, bool replace, CancellationToken ct = default)
        => ExecuteAsync("import the sheet", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/sheet/import", new ImportSheetRequest(csv, replace), JsonOptions, ct);
            await ThrowIfFailedAsync(response, "import the sheet", ct);
            return await response.Content.ReadFromJsonAsync<SheetImportResult>(JsonOptions, ct);
        });

    private Task<bool> SendNoContentAsync(string description, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
        => ExecuteAsync(description, async () =>
        {
            var response = await send();
            await ThrowIfFailedAsync(response, description, ct);
            return true;
        });

    // Assets -------------------------------------------------------------------

    public async Task<List<AssetDto>> GetAssetsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<AssetDto>>("/api/assets", "load assets", ct: ct) ?? [];

    public Task<AssetDto?> GetAssetAsync(Guid id, CancellationToken ct = default)
        => GetJsonAsync<AssetDto>($"/api/assets/{id}", "load asset", nullOn404: true, ct: ct);

    public Task<AssetSummaryDto?> GetAssetSummaryAsync(CancellationToken ct = default)
        => GetJsonAsync<AssetSummaryDto>("/api/assets/summary", "load asset summary", ct: ct);

    public async Task<List<AssetCategoryTemplateDto>> GetAssetCategoriesAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<AssetCategoryTemplateDto>>("/api/assets/categories", "load asset categories", ct: ct) ?? [];

    public Task<AssetDto?> CreateAssetAsync(CreateAssetRequest request, CancellationToken ct = default)
        => SendAssetAsync("add asset", () => _http.PostAsJsonAsync("/api/assets", request, JsonOptions, ct), ct);

    public Task<AssetDto?> UpdateAssetAsync(Guid id, UpdateAssetRequest request, CancellationToken ct = default)
        => SendAssetAsync("update asset", () => _http.PutAsJsonAsync($"/api/assets/{id}", request, JsonOptions, ct), ct);

    public Task<AssetDto?> RecordAssetValuationAsync(Guid id, RecordValuationRequest request, CancellationToken ct = default)
        => SendAssetAsync("record valuation", () => _http.PostAsJsonAsync($"/api/assets/{id}/valuations", request, JsonOptions, ct), ct);

    public async Task<AssetRefreshResultDto?> RefreshAssetAsync(Guid id, string? feed = null, CancellationToken ct = default)
    {
        var url = $"/api/assets/{id}/refresh" + (string.IsNullOrWhiteSpace(feed) ? "" : $"?feed={Uri.EscapeDataString(feed)}");
        return await ExecuteAsync("refresh asset valuation", async () =>
        {
            var response = await _http.PostAsync(url, null, ct);
            await ThrowIfFailedAsync(response, "refresh asset valuation", ct);
            return await response.Content.ReadFromJsonAsync<AssetRefreshResultDto>(JsonOptions, ct);
        });
    }

    public async Task DeleteAssetAsync(Guid id, CancellationToken ct = default)
        => await ExecuteAsync("remove asset", async () =>
        {
            var response = await _http.DeleteAsync($"/api/assets/{id}", ct);
            await ThrowIfFailedAsync(response, "remove asset", ct);
            return true;
        });

    private Task<AssetDto?> SendAssetAsync(string description, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
        => ExecuteAsync(description, async () =>
        {
            var response = await send();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await ThrowIfFailedAsync(response, description, ct);
            return await response.Content.ReadFromJsonAsync<AssetDto>(JsonOptions, ct);
        });

    /// <summary>Throws an <see cref="ApiClientException"/> carrying the API's 400 problem detail when present.</summary>
    private async Task ThrowIfFailedAsync(HttpResponseMessage response, string description, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        _logger.LogError("Non-success status {StatusCode} during {Operation}", (int)response.StatusCode, description);
        string? detail = null;
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("detail", out var d))
                {
                    detail = d.GetString();
                }
                else if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
                {
                    detail = string.Join(" ", errs.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetString())));
                }
            }
            catch (JsonException)
            {
                // Fall through to the generic message.
            }
        }

        throw new ApiClientException(
            detail is { Length: > 0 } ? detail : $"Couldn't {description} (server returned {(int)response.StatusCode}).",
            response.StatusCode);
    }

    // Query & reports ----------------------------------------------------------

    public Task<QuerySchemaDto?> GetQuerySchemaAsync(CancellationToken ct = default)
        => GetJsonAsync<QuerySchemaDto>("/api/query/schema", "load query schema", ct: ct);

    public Task<QueryRunOutcome> RunQueryAsync(string query, CancellationToken ct = default)
        => ExecuteAsync("run query", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/query/run", new { query }, JsonOptions, ct);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var message = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "The query is invalid.";
                var pos = doc.RootElement.TryGetProperty("position", out var p) && p.TryGetInt32(out var i) ? i : 0;
                return new QueryRunOutcome(null, message, pos);
            }

            await ThrowIfFailedAsync(response, "run query", ct);
            return new QueryRunOutcome(await response.Content.ReadFromJsonAsync<QueryResultDto>(JsonOptions, ct), null, 0);
        });

    public async Task<List<SavedReportDto>> GetReportsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<SavedReportDto>>("/api/reports", "load saved reports", ct: ct) ?? [];

    public Task<SavedReportDto?> GetReportAsync(string id, CancellationToken ct = default)
        => GetJsonAsync<SavedReportDto>($"/api/reports/{Uri.EscapeDataString(id)}", "load report", nullOn404: true, ct: ct);

    public Task<RunReportResultDto?> RunReportAsync(string id, CancellationToken ct = default)
        => GetJsonAsync<RunReportResultDto>($"/api/reports/{Uri.EscapeDataString(id)}/run", "run report", nullOn404: true, ct: ct);

    public Task<SavedReportDto?> SaveReportAsync(SaveReportRequest request, Guid? existingId = null, CancellationToken ct = default)
        => ExecuteAsync("save report", async () =>
        {
            var response = existingId is { } id
                ? await _http.PutAsJsonAsync($"/api/reports/{id}", request, JsonOptions, ct)
                : await _http.PostAsJsonAsync("/api/reports", request, JsonOptions, ct);
            await ThrowIfFailedAsync(response, "save report", ct);
            return await response.Content.ReadFromJsonAsync<SavedReportDto>(JsonOptions, ct);
        });

    public Task<bool> DeleteReportAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync("delete report", async () =>
        {
            var response = await _http.DeleteAsync($"/api/reports/{id}", ct);
            await ThrowIfFailedAsync(response, "delete report", ct);
            return true;
        });

    public Task<DashboardActivityDto?> GetDashboardActivityAsync(CancellationToken ct = default)
        => GetJsonAsync<DashboardActivityDto>("/api/dashboard/activity", "load dashboard activity", ct: ct);

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

    /// <summary>The verified data environment (demo / sandbox / production) of the API.</summary>
    public async Task<EnvironmentInfo?> GetEnvironmentAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read environment", async () =>
        {
            var response = await _http.GetAsync("/api/environment", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<EnvironmentInfo>(JsonOptions, ct)
                : null;
        });
    }

    /// <summary>Backup health, off-site state and provider API usage.</summary>
    public async Task<DataStatusDto?> GetDataStatusAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read data status", async () =>
        {
            var response = await _http.GetAsync("/api/data/status", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<DataStatusDto>(JsonOptions, ct)
                : null;
        });
    }

    /// <summary>Runs a verified backup now; throws with the server's reason when it fails.</summary>
    public async Task<BackupRunDto?> BackupNowAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("run backup", async () =>
        {
            var response = await _http.PostAsync("/api/data/backup-now", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException(
                    $"Backup failed (server returned {(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<BackupRunDto>(JsonOptions, ct);
        });
    }

    /// <summary>Downloads the full-data export zip (CSV per table + manifest).</summary>
    public async Task<byte[]?> ExportAllAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("export data", async () =>
        {
            var response = await _http.GetAsync("/api/data/export", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Export failed (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        });
    }

    /// <summary>Fills in missing categories (never overwrites existing ones).</summary>
    public async Task<CategorizationResultDto?> RunCategorizationAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("categorize transactions", async () =>
        {
            var response = await _http.PostAsync("/api/categorization/run", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't categorize (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<CategorizationResultDto>(JsonOptions, ct);
        });
    }

    /// <summary>Sets a transaction's category; optionally applies it to every uncategorized transaction of the same merchant.</summary>
    public async Task<int> SetTransactionCategoryAsync(Guid transactionId, Guid categoryId, bool applyToMerchant, CancellationToken ct = default)
    {
        return await ExecuteAsync("set category", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/transactions/{transactionId}/category",
                new { categoryId, applyToMerchant }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't set category (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return body.TryGetProperty("changed", out var c) ? c.GetInt32() : 1;
        });
    }

    /// <summary>Spending by category, 6-month cash flow and net-worth trend for the dashboard.</summary>
    public async Task<DashboardInsightsDto?> GetDashboardInsightsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read dashboard insights", async () =>
        {
            var response = await _http.GetAsync("/api/dashboard/insights", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<DashboardInsightsDto>(JsonOptions, ct)
                : null;
        });
    }

    public async Task<BudgetsDto?> GetBudgetsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read budgets", async () =>
        {
            var response = await _http.GetAsync("/api/budgets", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<BudgetsDto>(JsonOptions, ct) : null;
        });
    }

    public async Task SetBudgetAsync(string category, decimal monthlyLimit, CancellationToken ct = default)
    {
        await ExecuteAsync("save budget", async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/budgets", new { category, monthlyLimit }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save budget (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public async Task DeleteBudgetAsync(string category, CancellationToken ct = default)
    {
        await ExecuteAsync("remove budget", async () =>
        {
            var response = await _http.DeleteAsync($"/api/budgets?category={Uri.EscapeDataString(category)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't remove budget (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }
}
