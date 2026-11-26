using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
    public async Task<int> SetTransactionCategoryAsync(
        Guid transactionId, Guid categoryId, bool applyToMerchant, CancellationToken ct = default, uint? expectedVersion = null, Action<uint>? newVersion = null)
    {
        return await ExecuteAsync("set category", async () =>
        {
            var response = await SendVersionedAsync(HttpMethod.Put, $"/api/transactions/{transactionId}/category",
                new { categoryId, applyToMerchant }, expectedVersion, ct);
            ThrowIfChangedElsewhere(response, "transaction");
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't set category (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            if (ReadVersion(response) is { } version)
            {
                newVersion?.Invoke(version);
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return body.TryGetProperty("changed", out var c) ? c.GetInt32() : 1;
        });
    }

    /// <summary>Recategorizes one transaction, or (applyToAll) every transaction of the same merchant plus future syncs. Returns how many changed.</summary>
    public async Task<int> RecategorizeAsync(
        Guid transactionId, Guid categoryId, bool applyToAll, CancellationToken ct = default, string? note = null, uint? expectedVersion = null, Action<uint>? newVersion = null)
    {
        return await ExecuteAsync("recategorize", async () =>
        {
            var response = await SendVersionedAsync(HttpMethod.Put, $"/api/transactions/{transactionId}/recategorize",
                new { categoryId, applyToAll, note }, expectedVersion, ct);
            ThrowIfChangedElsewhere(response, "transaction");
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't recategorize (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            if (ReadVersion(response) is { } version)
            {
                newVersion?.Invoke(version);
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
}
