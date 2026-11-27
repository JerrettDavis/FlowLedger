namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
}
