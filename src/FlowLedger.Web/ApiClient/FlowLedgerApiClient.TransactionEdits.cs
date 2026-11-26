using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    public async Task SetTransactionNoteAsync(Guid transactionId, string note, CancellationToken ct = default)
    {
        await ExecuteAsync("save transaction note", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/transactions/{transactionId}/note", new { note }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't save note (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public async Task SetTransactionSplitsAsync(Guid transactionId, List<TransactionSplitDto> splits, CancellationToken ct = default)
    {
        await ExecuteAsync("save transaction splits", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/transactions/{transactionId}/splits", new { splits }, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode == System.Net.HttpStatusCode.BadRequest ? await response.Content.ReadAsStringAsync(ct) : "";
                throw new ApiClientException(detail.Contains("add up") ? "The splits must add up to the transaction amount." : $"Couldn't save splits (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }
}
