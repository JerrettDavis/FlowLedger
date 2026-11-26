using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // ── Home Assistant sharing ────────────────────────────────────────────────

    public async Task<HomeAssistantStateDto?> GetHomeAssistantAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read Home Assistant settings", async () =>
        {
            var response = await _http.GetAsync("/api/home-assistant", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<HomeAssistantStateDto>(JsonOptions, ct) : null;
        });
    }

    public async Task<HomeAssistantSettingsDto?> SaveHomeAssistantAsync(HomeAssistantSaveRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("save Home Assistant settings", async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/home-assistant", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException(
                    await ReadValidationMessageAsync(response, ct) ?? $"Couldn't save (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<HomeAssistantSettingsDto>(JsonOptions, ct);
        });
    }

    /// <summary>Sends a test event; returns null on success, otherwise a safe message to show.</summary>
    public async Task<string?> TestHomeAssistantAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("test Home Assistant", async () =>
        {
            var response = await _http.PostAsync("/api/home-assistant/test", null, ct);
            return response.IsSuccessStatusCode
                ? null
                : await ReadProblemDetailAsync(response, ct) ?? $"Test failed (server returned {(int)response.StatusCode}).";
        });
    }
}
