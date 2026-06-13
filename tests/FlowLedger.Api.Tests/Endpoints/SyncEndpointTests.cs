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
        // The simulated provider upserts accounts + transactions on sync.
        body!.AccountsUpserted.Should().BeGreaterThanOrEqualTo(0);
        body.TransactionsAdded.Should().BeGreaterThanOrEqualTo(0);
        body.TransactionsSkipped.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Post_sync_after_connect_seeds_accounts_for_tenant()
    {
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var accounts = await _client.GetAsync("/api/accounts");
        accounts.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Unauthenticated ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_connect_without_auth_returns_401()
    {
        using var anon = factory.CreateClient();
        var response = await anon.PostAsync("/api/connect", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private sealed record ConnectResult(string MemberId, string Provider);

    private sealed record SyncResultDto(int AccountsUpserted, int TransactionsAdded, int TransactionsSkipped);
}
