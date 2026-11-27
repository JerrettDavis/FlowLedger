using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // ── Linked logins (several IdP accounts → one tenant) ───────────────────────────────────────────
    public sealed record LinkedLoginDto(Guid UserId, string Issuer, string Subject, string? Email, string? DisplayName, DateTimeOffset LastLoginUtc);
    public sealed record LinkCodeDto(string Code, DateTimeOffset ExpiresUtc);

    public async Task<IReadOnlyList<LinkedLoginDto>> GetLinkedLoginsAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<LinkedLoginDto>>("/api/users/identities", "load linked logins", ct: ct) ?? [];

    public async Task<LinkCodeDto?> CreateLinkCodeAsync(CancellationToken ct = default) =>
        await ExecuteAsync("create link code", async () =>
        {
            var response = await _http.PostAsync("/api/users/link-code", null, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LinkCodeDto>(JsonOptions, ct) : null;
        });

    /// <summary>Links the signed-in login to the tenant that issued <paramref name="code"/>. True on success.</summary>
    public async Task<bool> RedeemLinkCodeAsync(Guid userId, string code, CancellationToken ct = default) =>
        await ExecuteAsync("redeem link code", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/users/link", new { userId, code }, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        });

    public async Task<bool> UnlinkLoginAsync(Guid userId, CancellationToken ct = default) =>
        await ExecuteAsync("unlink login", async () =>
            (await _http.DeleteAsync($"/api/users/identities/{userId}", ct)).IsSuccessStatusCode);

    public async Task<TransactionDto?> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken ct = default)
    {
        return await ExecuteAsync("create transaction", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/transactions", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogNonSuccessCreatingTransaction((int)response.StatusCode);
                throw new ApiClientException(
                    $"Couldn't create transaction (server returned {(int)response.StatusCode}).",
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<TransactionDto>(JsonOptions, ct);
        });
    }
}
