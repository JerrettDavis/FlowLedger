using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
}
