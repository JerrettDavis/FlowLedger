namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    public async Task<List<AccountPerformanceDto>?> GetInvestmentPerformanceAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("read investment performance", async () =>
        {
            var response = await _http.GetAsync("/api/investments/performance" + RangeQuery(from, to), ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<AccountPerformanceDto>>(JsonOptions, ct) : null;
        });
    }
}
