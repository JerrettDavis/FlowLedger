namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    public async Task SetTransactionNoteAsync(Guid transactionId, string note, CancellationToken ct = default, uint? expectedVersion = null, Action<uint>? newVersion = null)
    {
        await ExecuteAsync("save transaction note", async () =>
        {
            var response = await SendVersionedAsync(HttpMethod.Put, $"/api/transactions/{transactionId}/note", new { note }, expectedVersion, ct);
            ThrowIfChangedElsewhere(response, "transaction");
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save note (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            if (ReadVersion(response) is { } version)
            {
                newVersion?.Invoke(version);
            }

            return true;
        });
    }

    public async Task SetTransactionSplitsAsync(
        Guid transactionId, List<TransactionSplitDto> splits, CancellationToken ct = default, uint? expectedVersion = null, Action<uint>? newVersion = null)
    {
        await ExecuteAsync("save transaction splits", async () =>
        {
            var response = await SendVersionedAsync(HttpMethod.Put, $"/api/transactions/{transactionId}/splits", new { splits }, expectedVersion, ct);
            ThrowIfChangedElsewhere(response, "transaction");
            if (response.IsSuccessStatusCode && ReadVersion(response) is { } version)
            {
                newVersion?.Invoke(version);
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode == System.Net.HttpStatusCode.BadRequest ? await response.Content.ReadAsStringAsync(ct) : "";
                throw new ApiClientException(detail.Contains("add up") ? "The splits must add up to the transaction amount." : $"Couldn't save splits (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }
}
