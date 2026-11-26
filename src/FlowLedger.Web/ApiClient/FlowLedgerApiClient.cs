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
        => await GetAccountsAsync(includeInactive: false, ct);

    /// <summary>Lists accounts; includeInactive also returns disabled ones (for the management screen).</summary>
    public async Task<List<AccountDto>> GetAccountsAsync(bool includeInactive, CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<AccountDto>>(
            includeInactive ? "/api/accounts?includeInactive=true" : "/api/accounts", "load accounts", ct: ct);
        return result ?? [];
    }

    /// <summary>Enable/disable an account, set its mode (Standard/Holding) and whether its transactions are hidden.</summary>
    public async Task<AccountDto?> UpdateAccountSettingsAsync(Guid id, AccountSettingsRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("update account settings", async () =>
        {
            var response = await PutAccountAsync($"/api/accounts/{id}/settings", request, expectedVersion, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            ThrowIfChangedElsewhere(response);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} updating settings of account {Id}", (int)response.StatusCode, id);
                throw new ApiClientException(
                    $"Couldn't update account settings (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
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

    public async Task<AccountDto?> UpdateAccountAsync(Guid id, UpdateAccountRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("update account", async () =>
        {
            var response = await PutAccountAsync($"/api/accounts/{id}", request, expectedVersion, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            ThrowIfChangedElsewhere(response);

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

    /// <summary>Corrects an account's type by hand; the server pins it so provider sync never overwrites it.</summary>
    public async Task<AccountDto?> ChangeAccountTypeAsync(Guid id, ChangeAccountTypeRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("change account type", async () =>
        {
            var response = await PutAccountAsync($"/api/accounts/{id}/type", request, expectedVersion, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            ThrowIfChangedElsewhere(response);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} changing type of account {Id}", (int)response.StatusCode, id);
                var detail = response.StatusCode == HttpStatusCode.BadRequest
                    ? await ReadProblemDetailAsync(response, ct)
                    : null;
                throw new ApiClientException(
                    detail ?? $"Couldn't change account type (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
    }

    /// <summary>PUT with an <c>If-Match</c> precondition carrying the account's version (its ETag) when the caller has one.</summary>
    private Task<HttpResponseMessage> PutAccountAsync(string url, object body, uint? expectedVersion, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body, options: JsonOptions) };
        if (expectedVersion is { } version)
        {
            message.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return _http.SendAsync(message, ct);
    }

    /// <summary>A 409 means someone (another tab, a sync) changed the row after it was loaded; say so instead of a status code.</summary>
    private static void ThrowIfChangedElsewhere(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiClientException(ChangedElsewhereMessage, response.StatusCode);
        }
    }

    public const string ChangedElsewhereMessage = "This account was changed elsewhere (another tab or a sync). Reload to see the latest version, then try again.";

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                ? d.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<List<TransactionDto>> GetTransactionsAsync(
        Guid? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        bool includeHidden = false,
        string? search = null)
    {
        var url = TransactionsUrl(accountId, from, to, skip, take, includeHidden, search);
        var result = await GetJsonAsync<List<TransactionDto>>(url, "load transactions", ct: ct);
        return result ?? [];
    }

    /// <summary>One page of transactions plus the total number matching the filters (null when the server did not say).</summary>
    public async Task<TransactionPage> GetTransactionPageAsync(
        Guid? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        bool includeHidden = false,
        string? search = null)
    {
        var url = TransactionsUrl(accountId, from, to, skip, take, includeHidden, search);
        return await ExecuteAsync("load transactions", async () =>
        {
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} during {Operation}", (int)response.StatusCode, "load transactions");
                throw new ApiClientException(
                    $"Couldn't load transactions (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            int? total = response.Headers.TryGetValues("X-Total-Count", out var values)
                && int.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
            var items = await response.Content.ReadFromJsonAsync<List<TransactionDto>>(JsonOptions, ct) ?? [];
            return new TransactionPage(items, total);
        });
    }

    private static string TransactionsUrl(
        Guid? accountId, DateOnly? from, DateOnly? to, int skip, int take, bool includeHidden, string? search)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            qs.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (includeHidden)
        {
            qs.Add("includeHidden=true");
        }

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

        return "/api/transactions" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
    }

    // Bill sheet ---------------------------------------------------------------

    public Task<SheetViewDto?> GetSheetAsync(string? month = null, int months = 6, int ahead = 0, CancellationToken ct = default)
        => GetJsonAsync<SheetViewDto>(
            "/api/sheet?months=" + months + "&ahead=" + ahead + (string.IsNullOrWhiteSpace(month) ? "" : "&month=" + Uri.EscapeDataString(month)),
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

    public Task SetSheetPaymentAsync(Guid billId, string month, bool paid, decimal? amount = null, int slot = 0, CancellationToken ct = default)
        => SendNoContentAsync("update the cell", () => _http.PutAsJsonAsync(
            $"/api/sheet/bills/{billId}/payments/{month}", new SetPaymentRequest(paid, amount, slot), JsonOptions, ct), ct);

    /// <param name="slot">One payment of a multi-payment month, or null to reset the whole month.</param>
    public Task ClearSheetPaymentAsync(Guid billId, string month, int? slot = null, CancellationToken ct = default)
        => SendNoContentAsync("reset the cell", () => _http.DeleteAsync(
            $"/api/sheet/bills/{billId}/payments/{month}" + (slot is { } s ? $"?slot={s}" : ""), ct), ct);

    public Task<SheetImportResult?> ImportSheetAsync(string csv, bool replace, CancellationToken ct = default)
        => ExecuteAsync("import the sheet", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/sheet/import", new ImportSheetRequest(csv, replace), JsonOptions, ct);
            await ThrowIfFailedAsync(response, "import the sheet", ct);
            return await response.Content.ReadFromJsonAsync<SheetImportResult>(JsonOptions, ct);
        });

    public Task<List<ImportFormatDto>?> GetImportFormatsAsync(CancellationToken ct = default)
        => ExecuteAsync("load import formats", async () =>
        {
            var response = await _http.GetAsync("/api/imports/formats", ct);
            await ThrowIfFailedAsync(response, "load import formats", ct);
            return await response.Content.ReadFromJsonAsync<List<ImportFormatDto>>(JsonOptions, ct);
        });

    public Task<AutoImportResultDto?> ImportTransactionsAutoAsync(Guid accountId, string content, string? formatId, CancellationToken ct = default)
        => ExecuteAsync("import transactions", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/imports/auto", new { accountId, content, formatId }, JsonOptions, ct);
            await ThrowIfFailedAsync(response, "import transactions", ct);
            return await response.Content.ReadFromJsonAsync<AutoImportResultDto>(JsonOptions, ct);
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

    // Static reference data (built-in templates): fetched once per circuit, not on every page visit.
    private List<AssetCategoryTemplateDto>? _assetCategories;

    public async Task<List<AssetCategoryTemplateDto>> GetAssetCategoriesAsync(CancellationToken ct = default)
        => _assetCategories ??= await GetJsonAsync<List<AssetCategoryTemplateDto>>("/api/assets/categories", "load asset categories", ct: ct) ?? [];

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

    /// <summary>
    /// Throws an <see cref="ApiClientException"/> carrying the API's 400 problem detail when present, and the
    /// connector-neutral issue extensions (<see cref="ProblemExtensions"/>) for any failed status.
    /// </summary>
    private async Task ThrowIfFailedAsync(HttpResponseMessage response, string description, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        _logger.LogError("Non-success status {StatusCode} during {Operation}", (int)response.StatusCode, description);
        var problem = await ReadProblemAsync(response, ct);
        var detail = response.StatusCode == HttpStatusCode.BadRequest
            ? problem.Detail ?? problem.ValidationMessage
            : problem.Extensions is null ? null : problem.Detail; // issue problems carry a user-safe, secret-free detail
        throw new ApiClientException(
            detail is { Length: > 0 } ? detail : $"Couldn't {description} (server returned {(int)response.StatusCode}).",
            response.StatusCode,
            problem: problem.Extensions,
            fieldErrors: problem.FieldErrors);
    }

    private static async Task<ProblemDocument> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return ProblemDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ObjectDisposedException)
        {
            return ProblemDocument.Empty;
        }
    }

    /// <summary>"Couldn't X (server returned N): reason", with the issue extensions attached for the UI.</summary>
    private static async Task<ApiClientException> ProblemExceptionAsync(HttpResponseMessage response, string prefix, CancellationToken ct)
    {
        var problem = await ReadProblemAsync(response, ct);
        var reason = problem.ValidationMessage ?? problem.Detail ?? problem.Title;
        if (reason is { Length: > 200 })
        {
            reason = reason[..200];
        }

        return new ApiClientException(
            $"{prefix} (server returned {(int)response.StatusCode}){(string.IsNullOrEmpty(reason) ? string.Empty : ": " + reason)}",
            response.StatusCode,
            problem: problem.Extensions);
    }
    // Query & reports ----------------------------------------------------------

    public Task<QuerySchemaDto?> GetQuerySchemaAsync(CancellationToken ct = default)
        => GetJsonAsync<QuerySchemaDto>("/api/query/schema", "load query schema", ct: ct);

    public Task<AskQueryAnswer> AskQueryAsync(string question, CancellationToken ct = default)
        => ExecuteAsync("ask a question", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/query/ask", new { question }, JsonOptions, ct);
            await ThrowIfFailedAsync(response, "ask a question", ct);
            return await response.Content.ReadFromJsonAsync<AskQueryAnswer>(JsonOptions, ct)
                ?? new AskQueryAnswer(question, null, "No answer came back.", 0);
        });

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

    public Task<RunReportResultDto?> RunReportAsync(string id, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
        => GetJsonAsync<RunReportResultDto>($"/api/reports/{Uri.EscapeDataString(id)}/run{RangeQuery(from, to)}", "run report", nullOn404: true, ct: ct);

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

    /// <summary>Dismisses a flagged transaction from Suspicious Activity, optionally recording why.</summary>
    public async Task<bool> DismissSuspiciousAsync(Guid transactionId, string? note, CancellationToken ct = default) =>
        await ExecuteAsync("dismiss flagged transaction", async () =>
        {
            var response = await _http.PostAsJsonAsync($"/api/dashboard/suspicious/{transactionId}/dismiss", new { note }, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        });

    // ── Linked logins (several IdP accounts → one tenant) ───────────────────────────────────────────
    public sealed record LinkedLoginDto(Guid UserId, string Issuer, string Subject, string? Email, string? DisplayName, DateTimeOffset LastLoginUtc);
    public sealed record LinkCodeDto(string Code, DateTimeOffset ExpiresUtc);

    public async Task<IReadOnlyList<LinkedLoginDto>> GetLinkedLoginsAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<LinkedLoginDto>>("/api/users/identities", "load linked logins", ct: ct) ?? [];

    public async Task<LinkCodeDto?> CreateLinkCodeAsync(CancellationToken ct = default) =>
        await ExecuteAsync("create link code", async () =>
        {
            var response = await _http.PostAsync("/api/users/link-code", null, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LinkCodeDto>(JsonOptions, ct) : null;
        });

    /// <summary>Links the signed-in login to the tenant that issued <paramref name="code"/>. True on success.</summary>
    public async Task<bool> RedeemLinkCodeAsync(Guid userId, string code, CancellationToken ct = default) =>
        await ExecuteAsync("redeem link code", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/users/link", new { userId, code }, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        });

    public async Task<bool> UnlinkLoginAsync(Guid userId, CancellationToken ct = default) =>
        await ExecuteAsync("unlink login", async () =>
            (await _http.DeleteAsync($"/api/users/identities/{userId}", ct)).IsSuccessStatusCode);

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

    /// <summary>Creates a category at <paramref name="path"/> (e.g. "Food/Takeout") and returns it.</summary>
    public async Task<CategoryDto> CreateCategoryAsync(string path, CancellationToken ct = default)
    {
        return await ExecuteAsync("create category", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/categories",
                new { path, displayName = path.Split('/').Last(), parentId = (Guid?)null }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't create category (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return (await response.Content.ReadFromJsonAsync<CategoryDto>(JsonOptions, ct))!;
        });
    }

    /// <summary>Renames/moves a category path (and everything under it) to <paramref name="newPath"/>; existing paths there are merged into. <paramref name="dryRun"/> only previews.</summary>
    public async Task<CategoryMoveResultDto> MoveCategoryAsync(string path, string newPath, bool dryRun, CancellationToken ct = default)
    {
        return await ExecuteAsync("move category", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/categories/move", new { path, newPath, dryRun }, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode == System.Net.HttpStatusCode.BadRequest ? await ReadValidationMessageAsync(response, ct) : null;
                throw new ApiClientException(detail ?? $"Couldn't move category (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return (await response.Content.ReadFromJsonAsync<CategoryMoveResultDto>(JsonOptions, ct))!;
        });
    }

    private static async Task<string?> ReadValidationMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
            if (body.TryGetProperty("errors", out var errors))
            {
                foreach (var prop in errors.EnumerateObject())
                {
                    foreach (var msg in prop.Value.EnumerateArray())
                    {
                        return msg.GetString();
                    }
                }
            }
        }
        catch (Exception)
        {
            // fall through to the generic message
        }

        return null;
    }

    // ── Recurring Flows ───────────────────────────────────────────────────────

    public async Task<List<RecurringFlowDto>> GetRecurringFlowsAsync(CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<RecurringFlowDto>>(
            "/api/recurring-flows", "load recurring flows", ct: ct);
        return result ?? [];
    }

    public async Task<List<RecurringSuggestionDto>> GetRecurringSuggestionsAsync(CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<RecurringSuggestionDto>>(
            "/api/recurring-flows/suggestions", "load recurring suggestions", ct: ct);
        return result ?? [];
    }

    public async Task<bool> AcceptRecurringSuggestionAsync(string key, CancellationToken ct = default) =>
        await ExecuteAsync("accept recurring suggestion", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/recurring-flows/suggestions/accept", new { key }, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        });

    public async Task<bool> DismissRecurringSuggestionAsync(string key, CancellationToken ct = default) =>
        await ExecuteAsync("dismiss recurring suggestion", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/recurring-flows/suggestions/dismiss", new { key }, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        });

    public async Task<bool> FlagRecurringFlowNotRecurringAsync(Guid id, CancellationToken ct = default) =>
        await ExecuteAsync("flag recurring flow as not recurring", async () =>
        {
            var response = await _http.PostAsync($"/api/recurring-flows/{id}/flag-remove", null, ct);
            return response.IsSuccessStatusCode;
        });

    public async Task<AutoAddResultDto?> AutoAddRecurringFlowsAsync(CancellationToken ct = default) =>
        await ExecuteAsync("detect recurring flows", async () =>
        {
            var response = await _http.PostAsync("/api/recurring-flows/detect", null, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<AutoAddResultDto>(JsonOptions, ct)
                : null;
        });

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
        IReadOnlyCollection<Guid>? accountIds = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (accountIds is { Count: > 0 })
        {
            qs.Add("accounts=" + string.Join(",", accountIds));
        }

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
            await ThrowIfFailedAsync(response, "connect provider", ct);

            return await response.Content.ReadFromJsonAsync<ConnectResult>(JsonOptions, ct);
        });
    }

    public async Task<SyncResult?> SyncAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("sync data", async () =>
        {
            var response = await _http.PostAsync("/api/sync", null, ct);
            await ThrowIfFailedAsync(response, "sync data", ct);

            return await response.Content.ReadFromJsonAsync<SyncResult>(JsonOptions, ct);
        });
    }

    /// <summary>Spending/income breakdown by category (drill-down), tag, merchant or account, with trend and transactions.</summary>
    public async Task<BreakdownResultDto?> GetBreakdownAsync(
        DateOnly from, DateOnly to, bool income, string groupBy, string? parent, string? select, string bucket,
        IReadOnlyCollection<Guid>? accountIds = null, bool includeTransfers = false, CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            $"includeTransfers={includeTransfers.ToString().ToLowerInvariant()}", $"from={from:yyyy-MM-dd}", $"to={to:yyyy-MM-dd}", $"type={(income ? "income" : "spending")}",
            $"groupBy={Uri.EscapeDataString(groupBy)}", $"bucket={bucket}",
        };
        if (!string.IsNullOrWhiteSpace(parent))
        {
            qs.Add($"parent={Uri.EscapeDataString(parent)}");
        }

        if (!string.IsNullOrWhiteSpace(select))
        {
            qs.Add($"select={Uri.EscapeDataString(select)}");
        }

        if (accountIds is { Count: > 0 })
        {
            qs.Add("accounts=" + string.Join(",", accountIds));
        }

        return await GetJsonAsync<BreakdownResultDto>("/api/insights/breakdown?" + string.Join("&", qs), "load spending breakdown", ct: ct);
    }

    /// <summary>Last successful sync time and the server's enforced minimum interval (no provider call).</summary>
    public async Task<SyncStatusDto?> GetSyncStatusAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read sync status", async () =>
        {
            var response = await _http.GetAsync("/api/sync/status", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<SyncStatusDto>(JsonOptions, ct)
                : null;
        });

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
    public async Task<CategorizationResultDto?> RunCategorizationAsync(CancellationToken ct = default, bool useLlm = false)
    {
        return await ExecuteAsync("categorize transactions", async () =>
        {
            var response = await _http.PostAsync("/api/categorization/run" + (useLlm ? "?llm=true" : string.Empty), null, ct);
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

    /// <summary>Recategorizes one transaction, or (applyToAll) every transaction of the same merchant plus future syncs. Returns how many changed.</summary>
    public async Task<int> RecategorizeAsync(Guid transactionId, Guid categoryId, bool applyToAll, CancellationToken ct = default, string? note = null)
    {
        return await ExecuteAsync("recategorize", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/transactions/{transactionId}/recategorize",
                new { categoryId, applyToAll, note }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't recategorize (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return body.TryGetProperty("changed", out var c) ? c.GetInt32() : 1;
        });
    }

    /// <summary>Labels every transaction whose bank description contains <paramref name="pattern"/> as <paramref name="alias"/> (now and after future syncs). Returns how many were renamed.</summary>
    public async Task<int> SetMerchantAliasAsync(string pattern, string alias, CancellationToken ct = default)
    {
        return await ExecuteAsync("save alias", async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/merchant-aliases", new { pattern, alias }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save alias (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return body.TryGetProperty("changed", out var c) ? c.GetInt32() : 0;
        });
    }

    /// <summary>
    /// Per-item change of a dated series over a window (balance_history by account, asset_valuations by asset):
    /// the earliest and latest value inside [from, to]. Items with fewer than one point are omitted.
    /// </summary>
    public async Task<Dictionary<string, (decimal Start, decimal End)>> GetSeriesChangesAsync(
        string dataset, string keyColumn, string valueColumn, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var clauses = new List<string>();
        if (from is { } f)
        {
            clauses.Add($"date >= \"{f:yyyy-MM-dd}\"");
        }

        if (to is { } t)
        {
            clauses.Add($"date <= \"{t:yyyy-MM-dd}\"");
        }

        var outcome = await RunQueryAsync(dataset + (clauses.Count > 0 ? " | where " + string.Join(" and ", clauses) : ""), ct);
        var result = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase);
        if (outcome.Result is not { } r)
        {
            return result;
        }

        int Col(string n) => r.Columns.FindIndex(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        int di = Col("date"), ki = Col(keyColumn), vi = Col(valueColumn);
        if (di < 0 || ki < 0 || vi < 0)
        {
            return result;
        }

        foreach (var g in r.Rows.GroupBy(row => row[ki].ToString()))
        {
            var ordered = g.OrderBy(row => row[di].ToString(), StringComparer.Ordinal).ToList();
            if (ordered.Count == 0 || g.Key is null)
            {
                continue;
            }

            decimal V(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDecimal() : 0m;
            result[g.Key] = (V(ordered[0][vi]), V(ordered[^1][vi]));
        }

        return result;
    }

    private static string RangeQuery(DateOnly? from, DateOnly? to)
    {
        var parts = new List<string>();
        if (from is { } f)
        {
            parts.Add($"from={f:yyyy-MM-dd}");
        }

        if (to is { } t)
        {
            parts.Add($"to={t:yyyy-MM-dd}");
        }

        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    /// <summary>Spending by category, 6-month cash flow and net-worth trend for the dashboard.</summary>
    public async Task<DashboardInsightsDto?> GetDashboardInsightsAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("read dashboard insights", async () =>
        {
            var response = await _http.GetAsync("/api/dashboard/insights" + RangeQuery(from, to), ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<DashboardInsightsDto>(JsonOptions, ct)
                : null;
        });
    }

    public async Task<BudgetsDto?> GetBudgetsAsync(DateOnly? month = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("read budgets", async () =>
        {
            var response = await _http.GetAsync(month is { } m ? $"/api/budgets?month={m:yyyy-MM}" : "/api/budgets", ct);
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

    private async Task<T?> StrategyCallAsync<T>(string what, HttpMethod method, string url, object? body, CancellationToken ct)
    {
        return await ExecuteAsync(what, async () =>
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                req.Content = JsonContent.Create(body);
            }

            var response = await _http.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't {what} (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        });
    }

    public Task<StrategyReportDto?> GetStrategiesAsync(CancellationToken ct = default) =>
        StrategyCallAsync<StrategyReportDto>("load strategies", HttpMethod.Get, "/api/strategies", null, ct);

    public Task<List<AccountRateDto>?> GetAccountRatesAsync(CancellationToken ct = default) =>
        StrategyCallAsync<List<AccountRateDto>>("load account rates", HttpMethod.Get, "/api/strategies/account-rates", null, ct);

    public async Task SaveAccountRateAsync(Guid accountId, decimal ratePercent, int? remainingMonths, CancellationToken ct = default)
    {
        await ExecuteAsync("save the account rate", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/strategies/account-rates/{accountId}", new { ratePercent, remainingMonths }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save the rate (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public async Task RemoveAccountRateAsync(Guid accountId, CancellationToken ct = default)
    {
        await ExecuteAsync("remove the account rate", async () =>
        {
            var response = await _http.DeleteAsync($"/api/strategies/account-rates/{accountId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't remove the rate (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public Task<GrowthComparisonDto?> SimulateGrowthAsync(decimal start, decimal baseMonthly, decimal baseApy, decimal scenarioMonthly, decimal scenarioApy, int years, CancellationToken ct = default) =>
        StrategyCallAsync<GrowthComparisonDto>("run the growth simulation", HttpMethod.Post, "/api/strategies/simulate/growth",
            new { start, baseMonthly, baseApy, scenarioMonthly, scenarioApy, years }, ct);

    public Task<FireDto?> SimulateFireAsync(decimal invested, decimal monthlyContribution, decimal annualSpend, decimal withdrawalRate, decimal realReturn, decimal? age, int years, CancellationToken ct = default) =>
        StrategyCallAsync<FireDto>("run the FIRE projection", HttpMethod.Post, "/api/strategies/simulate/fire",
            new { invested, monthlyContribution, annualSpend, withdrawalRate, realReturn, age, years }, ct);

    public Task<PastWhatIfDto?> SimulatePastAsync(int months, decimal extraMonthly, string? cutCategory, decimal cutPercent, decimal idleCash, decimal fromApy, decimal toApy, decimal growthApy, CancellationToken ct = default) =>
        StrategyCallAsync<PastWhatIfDto>("run the what-if", HttpMethod.Post, "/api/strategies/simulate/past",
            new { months, extraMonthly, cutCategory, cutPercent, idleCash, fromApy, toApy, growthApy }, ct);

    public Task<RefinanceDto?> SimulateRefinanceAsync(decimal balance, decimal currentRate, int remainingMonths, decimal newRate, int newTermMonths, decimal closingCosts, CancellationToken ct = default) =>
        StrategyCallAsync<RefinanceDto>("run the refinance calculation", HttpMethod.Post, "/api/strategies/simulate/refinance",
            new { balance, currentRate, remainingMonths, newRate, newTermMonths, closingCosts }, ct);

    public Task<GoalPlanDto?> SimulateGoalAsync(
        decimal target, decimal saved, decimal monthly, decimal apy, int deadlineMonths, decimal extraMonthly,
        decimal incomeRaisePct, decimal spendCutPct, decimal lumpSum, decimal scenarioApy, CancellationToken ct = default) =>
        StrategyCallAsync<GoalPlanDto>("plan the goal", HttpMethod.Post, "/api/strategies/simulate/goal",
            new { target, saved, monthly, apy, deadlineMonths, extraMonthly, incomeRaisePct, spendCutPct, lumpSum, scenarioApy }, ct);

    public Task<LadderPlanDto?> SimulateLadderAsync(decimal amount, CancellationToken ct = default) =>
        StrategyCallAsync<LadderPlanDto>("build the CD ladder", HttpMethod.Post, "/api/strategies/simulate/ladder", new { amount }, ct);

    public async Task<DebtPlanDto?> PlanDebtAsync(List<DebtRowDto> debts, decimal extraMonthly, CancellationToken ct = default)
    {
        return await ExecuteAsync("plan debt payoff", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/debt/plan", new { debts, extraMonthly }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't compute plan (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<DebtPlanDto>(JsonOptions, ct);
        });
    }

    public async Task<List<AlertDto>?> GetAlertsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read alerts", async () =>
        {
            var response = await _http.GetAsync("/api/alerts", ct);
            await ThrowIfFailedAsync(response, "read alerts", ct);
            return await response.Content.ReadFromJsonAsync<List<AlertDto>>(JsonOptions, ct);
        });
    }

    public async Task<List<CategoryRuleDto>?> GetCategoryRulesAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read category rules", async () =>
        {
            var response = await _http.GetAsync("/api/category-rules", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<CategoryRuleDto>>(JsonOptions, ct) : null;
        });
    }

    public async Task<int> SetCategoryRuleAsync(string pattern, string categoryPath, CancellationToken ct = default)
    {
        return await ExecuteAsync("save category rule", async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/category-rules", new { pattern, categoryPath }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save rule (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return body.TryGetProperty("changed", out var c) ? c.GetInt32() : 0;
        });
    }

    public async Task DeleteCategoryRuleAsync(string pattern, CancellationToken ct = default)
    {
        await ExecuteAsync("remove category rule", async () =>
        {
            var response = await _http.DeleteAsync($"/api/category-rules?pattern={Uri.EscapeDataString(pattern)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't remove rule (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public async Task<DuplicateScanDto?> ScanDuplicatesAsync(CancellationToken ct = default)
        => await ExecuteAsync("scan for duplicates", async () =>
        {
            var response = await _http.GetAsync("/api/duplicates", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<DuplicateScanDto>(JsonOptions, ct) : null;
        });

    public async Task<AutoMergeDto?> AutoMergeDuplicatesAsync(CancellationToken ct = default)
        => await ExecuteAsync("merge duplicates", async () =>
        {
            var response = await _http.PostAsync("/api/duplicates/auto-merge", null, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<AutoMergeDto>(JsonOptions, ct) : null;
        });

    public async Task MergeDuplicateAsync(Guid keepId, Guid removeId, CancellationToken ct = default)
        => await ExecuteAsync("merge transactions", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/duplicates/merge", new { firstId = keepId, secondId = removeId, keepId }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Could not merge (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });

    public async Task DismissDuplicateAsync(Guid firstId, Guid secondId, CancellationToken ct = default)
        => await ExecuteAsync("dismiss duplicate", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/duplicates/dismiss", new { firstId, secondId }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Could not dismiss (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });

    public async Task SetTransactionNoteAsync(Guid transactionId, string note, CancellationToken ct = default)
    {
        await ExecuteAsync("save transaction note", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/transactions/{transactionId}/note", new { note }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save note (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public async Task SetTransactionSplitsAsync(Guid transactionId, List<TransactionSplitDto> splits, CancellationToken ct = default)
    {
        await ExecuteAsync("save transaction splits", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/transactions/{transactionId}/splits", new { splits }, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode == System.Net.HttpStatusCode.BadRequest ? await response.Content.ReadAsStringAsync(ct) : "";
                throw new ApiClientException(detail.Contains("add up") ? "The splits must add up to the transaction amount." : $"Couldn't save splits (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public async Task<List<AccountPerformanceDto>?> GetInvestmentPerformanceAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("read investment performance", async () =>
        {
            var response = await _http.GetAsync("/api/investments/performance" + RangeQuery(from, to), ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<AccountPerformanceDto>>(JsonOptions, ct) : null;
        });
    }

    // ── Home Assistant sharing ────────────────────────────────────────────────

    public async Task<HomeAssistantStateDto?> GetHomeAssistantAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read Home Assistant settings", async () =>
        {
            var response = await _http.GetAsync("/api/home-assistant", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<HomeAssistantStateDto>(JsonOptions, ct) : null;
        });
    }

    public async Task<HomeAssistantSettingsDto?> SaveHomeAssistantAsync(HomeAssistantSaveRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("save Home Assistant settings", async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/home-assistant", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException(
                    await ReadValidationMessageAsync(response, ct) ?? $"Couldn't save (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<HomeAssistantSettingsDto>(JsonOptions, ct);
        });
    }

    /// <summary>Sends a test event; returns null on success, otherwise a safe message to show.</summary>
    public async Task<string?> TestHomeAssistantAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("test Home Assistant", async () =>
        {
            var response = await _http.PostAsync("/api/home-assistant/test", null, ct);
            return response.IsSuccessStatusCode
                ? null
                : await ReadProblemDetailAsync(response, ct) ?? $"Test failed (server returned {(int)response.StatusCode}).";
        });
    }

    // ── Integrations ──────────────────────────────────────────────────────────

    public async Task<IntegrationsStateDto?> GetIntegrationsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read integrations", async () =>
        {
            var response = await _http.GetAsync("/api/integrations", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<IntegrationsStateDto>(JsonOptions, ct) : null;
        });
    }

    public async Task<IntegrationSavedDto?> SaveIntegrationAsync(Guid? id, IntegrationSaveDto request, CancellationToken ct = default)
    {
        return await ExecuteAsync("save the integration", async () =>
        {
            var response = id is { } existing
                ? await _http.PutAsJsonAsync($"/api/integrations/{existing}", request, JsonOptions, ct)
                : await _http.PostAsJsonAsync("/api/integrations", request, JsonOptions, ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't save the integration", ct);
            return await response.Content.ReadFromJsonAsync<IntegrationSavedDto>(JsonOptions, ct);
        });
    }

    public async Task DeleteIntegrationAsync(Guid id, CancellationToken ct = default)
    {
        await ExecuteAsync("delete the integration", async () =>
        {
            var response = await _http.DeleteAsync($"/api/integrations/{id}", ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't delete the integration", ct);
            return true;
        });
    }

    public async Task<IntegrationSavedDto?> RotateIntegrationSecretAsync(Guid id, CancellationToken ct = default)
    {
        return await ExecuteAsync("rotate the signing secret", async () =>
        {
            var response = await _http.PostAsync($"/api/integrations/{id}/rotate-secret", null, ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't rotate the signing secret", ct);
            return await response.Content.ReadFromJsonAsync<IntegrationSavedDto>(JsonOptions, ct);
        });
    }

    public async Task<List<IntegrationDeliveryDto>?> GetIntegrationDeliveriesAsync(Guid id, CancellationToken ct = default)
    {
        return await ExecuteAsync("read the delivery log", async () =>
        {
            var response = await _http.GetAsync($"/api/integrations/{id}/deliveries", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<IntegrationDeliveryDto>>(JsonOptions, ct) : null;
        });
    }

    public Task<IntegrationCallResultDto?> TestIntegrationAsync(Guid id, CancellationToken ct = default)
        => PostIntegrationCallAsync($"/api/integrations/{id}/test", (object?)null, "send the test event", ct);

    public Task<IntegrationCallResultDto?> CallRestIntegrationAsync(Guid id, RestCallDto call, CancellationToken ct = default)
        => PostIntegrationCallAsync($"/api/integrations/{id}/rest/call", call, "send the request", ct);

    public Task<IntegrationCallResultDto?> InvokeSoapAsync(Guid id, SoapInvokeDto call, CancellationToken ct = default)
        => PostIntegrationCallAsync($"/api/integrations/{id}/soap/invoke", call, "call the operation", ct);

    public async Task<SoapOperationsDto?> GetSoapOperationsAsync(Guid id, CancellationToken ct = default)
    {
        return await ExecuteAsync("read the WSDL", async () =>
        {
            var response = await _http.PostAsync($"/api/integrations/{id}/soap/operations", null, ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't read the WSDL", ct);
            return await response.Content.ReadFromJsonAsync<SoapOperationsDto>(JsonOptions, ct);
        });
    }

    private async Task<IntegrationCallResultDto?> PostIntegrationCallAsync(string url, object? body, string what, CancellationToken ct)
    {
        return await ExecuteAsync(what, async () =>
        {
            var response = body is null
                ? await _http.PostAsync(url, null, ct)
                : await _http.PostAsync(url, System.Net.Http.Json.JsonContent.Create(body, body.GetType(), options: JsonOptions), ct);
            await ThrowIntegrationErrorAsync(response, $"Couldn't {what}", ct);
            return await response.Content.ReadFromJsonAsync<IntegrationCallResultDto>(JsonOptions, ct);
        });
    }

    private static async Task ThrowIntegrationErrorAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiClientException(
                await ReadValidationMessageAsync(response, ct) ?? await ReadProblemDetailAsync(response, ct)
                    ?? $"{fallback} (server returned {(int)response.StatusCode}).",
                response.StatusCode);
        }
    }

    public async Task<List<GoalDto>?> GetGoalsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read goals", async () =>
        {
            var response = await _http.GetAsync("/api/goals", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<GoalDto>>(JsonOptions, ct) : null;
        });
    }

    public async Task SaveGoalAsync(Guid? id, string name, decimal targetAmount, decimal savedAmount, DateOnly? targetDate, CancellationToken ct = default)
    {
        await ExecuteAsync("save goal", async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/goals", new { id, name, targetAmount, savedAmount, targetDate }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save goal (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public async Task DeleteGoalAsync(Guid id, CancellationToken ct = default)
    {
        await ExecuteAsync("remove goal", async () =>
        {
            var response = await _http.DeleteAsync($"/api/goals/{id}", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't remove goal (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    /// <summary>Begins (or resumes) the real bank connection and returns the provider widget URL. Billable on first use.</summary>
    /// <summary>Provider-specific connect info: SimpleFIN has no widget, so this returns the Bridge page to link institutions at.</summary>
    public async Task<ProviderInfoDto?> GetProviderInfoAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read provider info", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/provider", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ProviderInfoDto>(JsonOptions, ct)
                : null;
        });

    /// <summary>Connector overview (configured / active / usage / linked institutions). Null when the page is disabled or not allowed for this user.</summary>
    public async Task<ConnectorsOverviewDto?> GetConnectorsAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read connectors", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/connectors", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ConnectorsOverviewDto>(JsonOptions, ct)
                : null;
        });

    // ── Local AI ────────────────────────────────────────────────────────────

    public async Task<LocalAiStatusDto?> GetLocalAiStatusAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read local AI status", async () =>
        {
            var response = await _http.GetAsync("/api/local-ai/status", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LocalAiStatusDto>(JsonOptions, ct) : null;
        });

    /// <summary>Starts a catalog model download. Returns null on success, else a short safe reason.</summary>
    public async Task<string?> StartLocalAiDownloadAsync(string modelId, CancellationToken ct = default) =>
        await ExecuteAsync("start the model download", async () =>
        {
            var response = await _http.PostAsync($"/api/local-ai/models/{Uri.EscapeDataString(modelId)}/download", null, ct);
            if (response.IsSuccessStatusCode)
            {
                return (string?)null;
            }

            return response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "Only the household owner can install models."
                : await ReadProblemDetailAsync(response, ct) ?? $"The server returned {(int)response.StatusCode}.";
        });

    public async Task<bool> RemoveLocalAiModelAsync(string modelId, CancellationToken ct = default) =>
        await ExecuteAsync("remove the model", async () =>
            (await _http.DeleteAsync($"/api/local-ai/models/{Uri.EscapeDataString(modelId)}", ct)).IsSuccessStatusCode);

    public async Task<MerchantGroupsDto?> GetMerchantGroupsAsync(double? threshold = null, CancellationToken ct = default) =>
        await ExecuteAsync("find similar merchants", async () =>
        {
            var url = "/api/local-ai/merchant-groups" + (threshold is { } t ? $"?threshold={t.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "");
            var response = await _http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<MerchantGroupsDto>(JsonOptions, ct) : null;
        });

    public async Task<EvaluationDto?> EvaluateLocalAiAsync(int folds = 5, CancellationToken ct = default) =>
        await ExecuteAsync("evaluate the categorizers", async () =>
        {
            var response = await _http.PostAsync($"/api/local-ai/evaluate?folds={folds}", null, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<EvaluationDto>(JsonOptions, ct) : null;
        });

    public async Task<List<SimilarTransactionDto>> GetSimilarTransactionsAsync(Guid transactionId, int top = 10, CancellationToken ct = default) =>
        await ExecuteAsync("find similar transactions", async () =>
        {
            var response = await _http.GetAsync($"/api/local-ai/similar/{transactionId}?top={top}", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<SimilarTransactionDto>>(JsonOptions, ct) ?? [] : [];
        });

    /// <summary>Which connector feeds each of this user's accounts.</summary>
    public async Task<List<AccountConnectorLinkDto>> GetAccountConnectorsAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read account connectors", async () =>
        {
            var response = await _http.GetAsync("/api/accounts/connectors", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<AccountConnectorLinkDto>>(JsonOptions, ct) ?? []
                : [];
        });

    /// <summary>Provider accounts a connector has seen that no account uses yet.</summary>
    public async Task<List<AvailableProviderAccountDto>> GetAvailableProviderAccountsAsync(string connectorId, CancellationToken ct = default) =>
        await ExecuteAsync("read available provider accounts", async () =>
        {
            var response = await _http.GetAsync($"/api/integrations/connectors/{Uri.EscapeDataString(connectorId)}/available-accounts", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<AvailableProviderAccountDto>>(JsonOptions, ct) ?? []
                : [];
        });

    /// <summary>Feeds an account from a connector's provider account, or pins it manual (<c>connectorId = "manual"</c>).</summary>
    public async Task SetAccountConnectorAsync(Guid accountId, SetAccountConnectorRequest request, CancellationToken ct = default) =>
        await ExecuteAsync("set account connector", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/accounts/{accountId}/connector", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw await ProblemExceptionAsync(response, "Couldn't change the data source", ct);
            }

            return true;
        });

    /// <summary>Removes an account's connector link.</summary>
    public async Task ClearAccountConnectorAsync(Guid accountId, CancellationToken ct = default) =>
        await ExecuteAsync("clear account connector", async () =>
        {
            var response = await _http.DeleteAsync($"/api/accounts/{accountId}/connector", ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw await ProblemExceptionAsync(response, "Couldn't clear the data source", ct);
            }

            return true;
        });

    /// <summary>The signed-in tenant's own connections and their health; empty on any failure (never blocks the shell).</summary>
    public async Task<IReadOnlyList<MyConnectionDto>> GetMyConnectionsAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read connections", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/connections", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<MyConnectionDto>>(JsonOptions, ct) ?? []
                : [];
        }) ?? [];

    /// <summary>Whether the in-app Quiltt Connector is enabled (false on any failure, so callers fall back to the old flow).</summary>
    public async Task<QuilttConfigDto> GetQuilttConfigAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read Quiltt config", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/quiltt/config", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<QuilttConfigDto>(JsonOptions, ct) ?? new QuilttConfigDto(false)
                : new QuilttConfigDto(false);
        }) ?? new QuilttConfigDto(false);

    /// <summary>Mints a session so the browser can open the Quiltt Connector for the signed-in user.</summary>
    public async Task<QuilttSessionDto?> StartQuilttSessionAsync(CancellationToken ct = default) =>
        await ExecuteAsync("start Quiltt session", async () =>
        {
            var response = await _http.PostAsync("/api/integrations/quiltt/session", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw await ProblemExceptionAsync(response, "Couldn't start the bank connection", ct);
            }

            return await response.Content.ReadFromJsonAsync<QuilttSessionDto>(JsonOptions, ct);
        });

    public async Task<string?> BeginBankConnectionAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("connect bank", async () =>
        {
            var response = await _http.PostAsync("/api/integrations/mx/connect-token", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException(
                    $"Couldn't start the bank connection (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return body.TryGetProperty("widgetUrl", out var u) ? u.GetString() : null;
        });
    }
}
