using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    /// <summary>
    /// Per-item change of a dated series over a window (balance_history by account, asset_valuations by asset):
    /// the earliest and latest value inside [from, to]. Items with fewer than one point are omitted.
    /// </summary>
    public async Task<Dictionary<string, (decimal Start, decimal End)>> GetSeriesChangesAsync(
        string dataset, string keyColumn, string valueColumn, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var clauses = new List<string>();
        if (from is { } f)
        {
            clauses.Add($"date >= \"{f:yyyy-MM-dd}\"");
        }

        if (to is { } t)
        {
            clauses.Add($"date <= \"{t:yyyy-MM-dd}\"");
        }

        var outcome = await RunQueryAsync(dataset + (clauses.Count > 0 ? " | where " + string.Join(" and ", clauses) : ""), ct);
        var result = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase);
        if (outcome.Result is not { } r)
        {
            return result;
        }

        int Col(string n) => r.Columns.ToList().FindIndex(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        int di = Col("date"), ki = Col(keyColumn), vi = Col(valueColumn);
        if (di < 0 || ki < 0 || vi < 0)
        {
            return result;
        }

        foreach (var g in r.Rows.GroupBy(row => row[ki]?.ToString() ?? ""))
        {
            var ordered = g.OrderBy(row => row[di]?.ToString() ?? "", StringComparer.Ordinal).ToList();
            if (ordered.Count == 0 || g.Key is null)
            {
                continue;
            }

            decimal V(object? cell) => cell is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } e ? e.GetDecimal() : 0m;
            result[g.Key] = (V(ordered[0][vi]), V(ordered[^1][vi]));
        }

        return result;
    }

    private static string RangeQuery(DateOnly? from, DateOnly? to)
    {
        var parts = new List<string>();
        if (from is { } f)
        {
            parts.Add($"from={f:yyyy-MM-dd}");
        }

        if (to is { } t)
        {
            parts.Add($"to={t:yyyy-MM-dd}");
        }

        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    /// <summary>Spending by category, 6-month cash flow and net-worth trend for the dashboard.</summary>
    public async Task<DashboardInsightsDto?> GetDashboardInsightsAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("read dashboard insights", async () =>
        {
            var response = await _http.GetAsync("/api/dashboard/insights" + RangeQuery(from, to), ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<DashboardInsightsDto>(JsonOptions, ct)
                : null;
        });
    }
}
