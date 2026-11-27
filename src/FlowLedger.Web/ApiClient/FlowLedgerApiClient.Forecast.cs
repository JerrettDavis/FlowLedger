namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

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
}
