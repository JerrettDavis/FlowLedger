using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // ── Accounts ─────────────────────────────────────────────────────────────

    public async Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct = default)
        => await GetAccountsAsync(includeInactive: false, ct);

    /// <summary>Lists accounts; includeInactive also returns disabled ones (for the management screen).</summary>
    public async Task<List<AccountDto>> GetAccountsAsync(bool includeInactive, CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<AccountDto>>(
            includeInactive ? "/api/accounts?includeInactive=true" : "/api/accounts", "load accounts", ct: ct);
        return result ?? [];
    }

    /// <summary>Enable/disable an account, set its mode (Standard/Holding) and whether its transactions are hidden.</summary>
    public async Task<AccountDto?> UpdateAccountSettingsAsync(Guid id, AccountSettingsRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("update account settings", async () =>
        {
            var response = await PutAccountAsync($"/api/accounts/{id}/settings", request, expectedVersion, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            ThrowIfChangedElsewhere(response);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} updating settings of account {Id}", (int)response.StatusCode, id);
                throw new ApiClientException(
                    $"Couldn't update account settings (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
    }

    public async Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken ct = default)
        => await GetJsonAsync<AccountDto>(
            $"/api/accounts/{id}", "load account", nullOn404: true, ct: ct);

    public async Task<AccountDto?> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("create account", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/accounts", request, JsonOptions, ct);
            await ThrowIfFailedAsync(response, "create account", ct);

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
    }

    public async Task<AccountDto?> UpdateAccountAsync(Guid id, UpdateAccountRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("update account", async () =>
        {
            var response = await PutAccountAsync($"/api/accounts/{id}", request, expectedVersion, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            ThrowIfChangedElsewhere(response);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} updating account {Id}", (int)response.StatusCode, id);
                throw new ApiClientException(
                    $"Couldn't update account (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
    }

    /// <summary>Corrects an account's type by hand; the server pins it so provider sync never overwrites it.</summary>
    public async Task<AccountDto?> ChangeAccountTypeAsync(Guid id, ChangeAccountTypeRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        return await ExecuteAsync("change account type", async () =>
        {
            var response = await PutAccountAsync($"/api/accounts/{id}/type", request, expectedVersion, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            ThrowIfChangedElsewhere(response);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Non-success status {StatusCode} changing type of account {Id}", (int)response.StatusCode, id);
                var detail = response.StatusCode == HttpStatusCode.BadRequest
                    ? await ReadProblemDetailAsync(response, ct)
                    : null;
                throw new ApiClientException(
                    detail ?? $"Couldn't change account type (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<AccountDto>(JsonOptions, ct);
        });
    }

    /// <summary>PUT with an <c>If-Match</c> precondition carrying the account's version (its ETag) when the caller has one.</summary>
    private Task<HttpResponseMessage> PutAccountAsync(string url, object body, uint? expectedVersion, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body, options: JsonOptions) };
        if (expectedVersion is { } version)
        {
            message.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return _http.SendAsync(message, ct);
    }

    /// <summary>A 409 means someone (another tab, a sync) changed the row after it was loaded; say so instead of a status code.</summary>
    private static void ThrowIfChangedElsewhere(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiClientException(ChangedElsewhereMessage, response.StatusCode);
        }
    }

    public const string ChangedElsewhereMessage = "This account was changed elsewhere (another tab or a sync). Reload to see the latest version, then try again.";

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                ? d.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
