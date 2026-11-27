namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{
    /// <summary>Subscriptions found in the transactions, with price-change flags. Null when the server has none to report.</summary>
    public async Task<SubscriptionOverviewDto?> GetSubscriptionsAsync(CancellationToken ct = default)
        => await GetJsonAsync<SubscriptionOverviewDto>("/api/subscriptions", "load subscriptions", ct: ct);
}
