namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
}
