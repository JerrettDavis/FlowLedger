using System.Net;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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

        LogNonSuccessDuring((int)response.StatusCode, description);
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
}
