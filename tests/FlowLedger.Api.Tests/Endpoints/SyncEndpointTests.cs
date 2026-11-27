using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class SyncEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Connect ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_connect_returns_200_with_member_id_and_simulated_provider()
    {
        var response = await _client.PostAsync("/api/connect", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ConnectResult>();
        body.Should().NotBeNull();
        body!.Provider.Should().Be("Simulated");
        body.MemberId.Should().NotBeNullOrEmpty();
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_sync_returns_200_with_sync_result()
    {
        // Connect first so the simulated provider has state to sync.
        await _client.PostAsync("/api/connect", null);

        var response = await _client.PostAsync("/api/sync", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SyncResultDto>();
        body.Should().NotBeNull();
        // The simulated provider upserts accounts + transactions + recurring flows on sync.
        body!.AccountsUpserted.Should().BeGreaterThanOrEqualTo(0);
        body.TransactionsAdded.Should().BeGreaterThanOrEqualTo(0);
        body.TransactionsSkipped.Should().BeGreaterThanOrEqualTo(0);
        body.RecurringFlowsAdded.Should().BeGreaterThan(0, "simulated sync always seeds recurring flows on first run");
    }

    [Fact]
    public async Task Post_sync_after_connect_seeds_accounts_for_tenant()
    {
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var accounts = await _client.GetAsync("/api/accounts");
        accounts.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Estimate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Estimate_counts_only_accounts_linked_to_the_active_connector_and_skips_manual_ones()
    {
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var before = await GetEstimateAsync();
        before.ActiveAccounts.Should().BeGreaterThan(1);
        before.EstimatedProviderCalls.Should().Be(1 + before.ActiveAccounts);

        using var links = System.Text.Json.JsonDocument.Parse(await _client.GetStringAsync("/api/accounts/connectors"));
        var target = links.RootElement.EnumerateArray().First().GetProperty("accountId").GetGuid();
        (await _client.PutAsJsonAsync($"/api/accounts/{target}/connector", new { connectorId = "manual" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await GetEstimateAsync();
        after.ActiveAccounts.Should().Be(before.ActiveAccounts - 1, "a manual account is never fetched");
        after.EstimatedProviderCalls.Should().Be(1 + after.ActiveAccounts);
    }

    [Fact]
    public async Task Estimate_is_zero_accounts_for_a_tenant_with_no_links()
    {
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var estimate = await other.GetFromJsonAsync<EstimateDto>("/api/sync/estimate");
        estimate!.ActiveAccounts.Should().Be(0);
    }

    private async Task<EstimateDto> GetEstimateAsync() => (await _client.GetFromJsonAsync<EstimateDto>("/api/sync/estimate"))!;

    // ── Unauthenticated ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_connect_without_auth_returns_401()
    {
        using var anon = factory.CreateClient();
        var response = await anon.PostAsync("/api/connect", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private sealed record EstimateDto(int ActiveAccounts, int EstimatedProviderCalls);

    private sealed record ConnectResult(string MemberId, string Provider);

    private sealed record SyncResultDto(int AccountsUpserted, int TransactionsAdded, int TransactionsSkipped, int RecurringFlowsAdded);
}
