using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // ── Local AI ────────────────────────────────────────────────────────────

    public async Task<LocalAiStatusDto?> GetLocalAiStatusAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read local AI status", async () =>
        {
            var response = await _http.GetAsync("/api/local-ai/status", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LocalAiStatusDto>(JsonOptions, ct) : null;
        });

    /// <summary>Starts a catalog model download. Returns null on success, else a short safe reason.</summary>
    public async Task<string?> StartLocalAiDownloadAsync(string modelId, CancellationToken ct = default) =>
        await ExecuteAsync("start the model download", async () =>
        {
            var response = await _http.PostAsync($"/api/local-ai/models/{Uri.EscapeDataString(modelId)}/download", null, ct);
            if (response.IsSuccessStatusCode)
            {
                return (string?)null;
            }

            return response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "Only the household owner can install models."
                : await ReadProblemDetailAsync(response, ct) ?? $"The server returned {(int)response.StatusCode}.";
        });

    public async Task<bool> RemoveLocalAiModelAsync(string modelId, CancellationToken ct = default) =>
        await ExecuteAsync("remove the model", async () =>
            (await _http.DeleteAsync($"/api/local-ai/models/{Uri.EscapeDataString(modelId)}", ct)).IsSuccessStatusCode);

    public async Task<MerchantGroupsDto?> GetMerchantGroupsAsync(double? threshold = null, CancellationToken ct = default) =>
        await ExecuteAsync("find similar merchants", async () =>
        {
            var url = "/api/local-ai/merchant-groups" + (threshold is { } t ? $"?threshold={t.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "");
            var response = await _http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<MerchantGroupsDto>(JsonOptions, ct) : null;
        });

    public async Task<EvaluationDto?> EvaluateLocalAiAsync(int folds = 5, CancellationToken ct = default) =>
        await ExecuteAsync("evaluate the categorizers", async () =>
        {
            var response = await _http.PostAsync($"/api/local-ai/evaluate?folds={folds}", null, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<EvaluationDto>(JsonOptions, ct) : null;
        });

    public async Task<List<SimilarTransactionDto>> GetSimilarTransactionsAsync(Guid transactionId, int top = 10, CancellationToken ct = default) =>
        await ExecuteAsync("find similar transactions", async () =>
        {
            var response = await _http.GetAsync($"/api/local-ai/similar/{transactionId}?top={top}", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<SimilarTransactionDto>>(JsonOptions, ct) ?? [] : [];
        });

    /// <summary>Which connector feeds each of this user's accounts.</summary>
    public async Task<List<AccountConnectorLinkDto>> GetAccountConnectorsAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read account connectors", async () =>
        {
            var response = await _http.GetAsync("/api/accounts/connectors", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<AccountConnectorLinkDto>>(JsonOptions, ct) ?? []
                : [];
        });

    /// <summary>Provider accounts a connector has seen that no account uses yet.</summary>
    public async Task<List<AvailableProviderAccountDto>> GetAvailableProviderAccountsAsync(string connectorId, CancellationToken ct = default) =>
        await ExecuteAsync("read available provider accounts", async () =>
        {
            var response = await _http.GetAsync($"/api/integrations/connectors/{Uri.EscapeDataString(connectorId)}/available-accounts", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<AvailableProviderAccountDto>>(JsonOptions, ct) ?? []
                : [];
        });

    /// <summary>Feeds an account from a connector's provider account, or pins it manual (<c>connectorId = "manual"</c>).</summary>
    public async Task SetAccountConnectorAsync(Guid accountId, SetAccountConnectorRequest request, CancellationToken ct = default) =>
        await ExecuteAsync("set account connector", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/accounts/{accountId}/connector", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw await ProblemExceptionAsync(response, "Couldn't change the data source", ct);
            }

            return true;
        });

    /// <summary>Removes an account's connector link.</summary>
    public async Task ClearAccountConnectorAsync(Guid accountId, CancellationToken ct = default) =>
        await ExecuteAsync("clear account connector", async () =>
        {
            var response = await _http.DeleteAsync($"/api/accounts/{accountId}/connector", ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw await ProblemExceptionAsync(response, "Couldn't clear the data source", ct);
            }

            return true;
        });

    /// <summary>The signed-in tenant's own connections and their health; empty on any failure (never blocks the shell).</summary>
    public async Task<IReadOnlyList<MyConnectionDto>> GetMyConnectionsAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read connections", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/connections", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<MyConnectionDto>>(JsonOptions, ct) ?? []
                : [];
        }) ?? [];

    /// <summary>Whether the in-app Quiltt Connector is enabled (false on any failure, so callers fall back to the old flow).</summary>
    public async Task<QuilttConfigDto> GetQuilttConfigAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read Quiltt config", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/quiltt/config", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<QuilttConfigDto>(JsonOptions, ct) ?? new QuilttConfigDto(false)
                : new QuilttConfigDto(false);
        }) ?? new QuilttConfigDto(false);

    /// <summary>Mints a session so the browser can open the Quiltt Connector for the signed-in user.</summary>
    public async Task<QuilttSessionDto?> StartQuilttSessionAsync(CancellationToken ct = default) =>
        await ExecuteAsync("start Quiltt session", async () =>
        {
            var response = await _http.PostAsync("/api/integrations/quiltt/session", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw await ProblemExceptionAsync(response, "Couldn't start the bank connection", ct);
            }

            return await response.Content.ReadFromJsonAsync<QuilttSessionDto>(JsonOptions, ct);
        });

    public async Task<string?> BeginBankConnectionAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("connect bank", async () =>
        {
            var response = await _http.PostAsync("/api/integrations/mx/connect-token", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException(
                    $"Couldn't start the bank connection (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return body.TryGetProperty("widgetUrl", out var u) ? u.GetString() : null;
        });
    }
}
