using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<List<TransactionDto>> GetTransactionsAsync(
        Guid? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        bool includeHidden = false,
        string? search = null)
    {
        var url = TransactionsUrl(accountId, from, to, skip, take, includeHidden, search);
        var result = await GetJsonAsync<List<TransactionDto>>(url, "load transactions", ct: ct);
        return result ?? [];
    }

    /// <summary>One page of transactions plus the total number matching the filters (null when the server did not say).</summary>
    public async Task<TransactionPage> GetTransactionPageAsync(
        Guid? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        bool includeHidden = false,
        string? search = null)
    {
        var url = TransactionsUrl(accountId, from, to, skip, take, includeHidden, search);
        return await ExecuteAsync("load transactions", async () =>
        {
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} during {Operation}", (int)response.StatusCode, "load transactions");
                throw new ApiClientException(
                    $"Couldn't load transactions (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            int? total = response.Headers.TryGetValues("X-Total-Count", out var values)
                && int.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
            var items = await response.Content.ReadFromJsonAsync<List<TransactionDto>>(JsonOptions, ct) ?? [];
            return new TransactionPage(items, total);
        });
    }

    private static string TransactionsUrl(
        Guid? accountId, DateOnly? from, DateOnly? to, int skip, int take, bool includeHidden, string? search)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            qs.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (includeHidden)
        {
            qs.Add("includeHidden=true");
        }

        if (accountId.HasValue)
        {
            qs.Add($"accountId={accountId}");
        }

        if (from.HasValue)
        {
            qs.Add($"from={from:yyyy-MM-dd}");
        }

        if (to.HasValue)
        {
            qs.Add($"to={to:yyyy-MM-dd}");
        }

        if (skip > 0)
        {
            qs.Add($"skip={skip}");
        }

        if (take != 100)
        {
            qs.Add($"take={take}");
        }

        return "/api/transactions" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
    }
}
