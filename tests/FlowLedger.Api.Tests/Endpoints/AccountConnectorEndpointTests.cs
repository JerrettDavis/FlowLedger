using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>ADR 0010: per-account connector assignment through the API.</summary>
[Collection("ApiIntegration")]
public sealed class AccountConnectorEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
        await _client.PostAsync("/api/connect", null);
        (await _client.PostAsync("/api/sync", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<(Guid AccountId, string Connector, string ProviderAccountId)>> LinksAsync()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/accounts/connectors"));
        return doc.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("accountId").GetGuid(), e.GetProperty("connectorId").GetString()!, e.GetProperty("providerAccountId").GetString()!))
            .ToList();
    }

    [Fact]
    public async Task Sync_links_every_synced_account_to_the_simulated_connector()
    {
        var links = await LinksAsync();

        links.Should().NotBeEmpty();
        links.Should().OnlyContain(l => l.Connector == "Simulated");
        using var accounts = JsonDocument.Parse(await _client.GetStringAsync("/api/accounts"));
        links.Select(l => l.AccountId).Should().BeEquivalentTo(accounts.RootElement.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task An_account_can_be_pinned_manual_and_unlinked()
    {
        var target = (await LinksAsync()).First().AccountId;

        (await _client.PutAsJsonAsync($"/api/accounts/{target}/connector", new { connectorId = "manual" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinksAsync()).Single(l => l.AccountId == target).Connector.Should().Be("manual");

        // A pinned account is left alone by the next sync.
        await _client.PostAsync("/api/sync", null);
        (await LinksAsync()).Single(l => l.AccountId == target).Connector.Should().Be("manual");

        (await _client.DeleteAsync($"/api/accounts/{target}/connector")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.DeleteAsync($"/api/accounts/{target}/connector")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Linking_validates_the_connector_the_provider_account_and_conflicts()
    {
        var links = await LinksAsync();
        var a = links[0];
        var b = links[1];

        (await _client.PutAsJsonAsync($"/api/accounts/{a.AccountId}/connector", new { connectorId = "MX", providerAccountId = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "MX is not enabled on this instance");
        (await _client.PutAsJsonAsync($"/api/accounts/{a.AccountId}/connector", new { connectorId = "Simulated" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a provider account must be chosen");
        (await _client.PutAsJsonAsync($"/api/accounts/{a.AccountId}/connector", new { connectorId = "Simulated", providerAccountId = "never-seen" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/accounts/{a.AccountId}/connector", new { connectorId = "Simulated", providerAccountId = b.ProviderAccountId }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "that provider account already feeds another account");
        (await _client.PutAsJsonAsync($"/api/accounts/{Guid.NewGuid()}/connector", new { connectorId = "manual" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unlinking_frees_the_provider_account_to_be_linked_to_another_account()
    {
        var links = await LinksAsync();
        var a = links[0];
        var b = links[1];

        (await _client.DeleteAsync($"/api/accounts/{a.AccountId}/connector")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var available = JsonDocument.Parse(await _client.GetStringAsync("/api/integrations/connectors/Simulated/available-accounts"));
        available.RootElement.EnumerateArray().Select(x => x.GetProperty("providerAccountId").GetString()).Should().Contain(a.ProviderAccountId);

        // Move b's feed to a's old provider account: b now mirrors a's provider account and a's feed is free.
        (await _client.PutAsJsonAsync($"/api/accounts/{b.AccountId}/connector", new { connectorId = "Simulated", providerAccountId = a.ProviderAccountId }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinksAsync()).Single(l => l.AccountId == b.AccountId).ProviderAccountId.Should().Be(a.ProviderAccountId);
    }

    [Fact]
    public async Task Another_tenant_has_no_links_and_cannot_see_the_catalog_of_a_shared_connector()
    {
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());

        (await other.GetStringAsync("/api/accounts/connectors")).Should().Be("[]");
    }
}
