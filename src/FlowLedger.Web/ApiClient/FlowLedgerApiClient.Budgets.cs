using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
}
