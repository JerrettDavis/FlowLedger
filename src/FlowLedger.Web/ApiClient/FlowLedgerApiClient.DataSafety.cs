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

    /// <summary>Downloads the signed-in tenant's export zip (CSV per table + manifest). Single-tenant bundles cannot be restored with import.</summary>
    public Task<byte[]?> ExportMyDataAsync(CancellationToken ct = default) => ExportAsync("/api/data/export", ct);

    /// <summary>Downloads the WHOLE-database dump (every tenant; disaster recovery only; importable into an empty database).</summary>
    public Task<byte[]?> ExportWholeDatabaseAsync(CancellationToken ct = default) => ExportAsync("/api/data/export?scope=all", ct);

    private async Task<byte[]?> ExportAsync(string path, CancellationToken ct)
    {
        return await ExecuteAsync("export data", async () =>
        {
            var response = await _http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Export failed (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        });
    }
}
