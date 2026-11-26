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

        var url = "/api/transactions" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        var result = await GetJsonAsync<List<TransactionDto>>(url, "load transactions", ct: ct);
        return result ?? [];
    }
}
