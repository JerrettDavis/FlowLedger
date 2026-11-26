using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
}
