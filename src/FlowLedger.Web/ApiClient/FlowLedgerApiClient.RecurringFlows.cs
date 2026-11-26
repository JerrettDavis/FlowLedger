using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
}
