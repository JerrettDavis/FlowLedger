using System.Net.Http.Json;
using System.Text.Json;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#70: GET /api/subscriptions finds steady recurring charges in the caller's own transactions and flags a price change.</summary>
[Collection("ApiIntegration")]
public sealed class SubscriptionEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", CreateAccountRequestBuilder.A().Named("Subs").OfType("Checking").StartingAt(0m).Build());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task ChargeAsync(HttpClient client, Guid account, decimal amount, DateOnly date)
        => (await client.PostAsJsonAsync("/api/transactions", new CreateTransactionRequest(account, amount, "USD", "Debit", "Streamly Video", date, null, null, null, null)))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task A_steady_monthly_charge_is_listed_with_its_price_increase_and_only_for_its_owner()
    {
        var account = await CreateAccountAsync(_client);
        var latest = factory.ServerToday.AddDays(-3);
        await ChargeAsync(_client, account, 15.99m, latest.AddDays(-90));
        await ChargeAsync(_client, account, 15.99m, latest.AddDays(-60));
        await ChargeAsync(_client, account, 15.99m, latest.AddDays(-30));
        await ChargeAsync(_client, account, 17.99m, latest);

        var overview = await _client.GetFromJsonAsync<JsonElement>("/api/subscriptions");

        var sub = overview.GetProperty("subscriptions").EnumerateArray().Should().ContainSingle().Subject;
        sub.GetProperty("name").GetString().Should().Be("Streamly Video");
        sub.GetProperty("cadence").GetString().Should().Be("Monthly");
        sub.GetProperty("amount").GetDecimal().Should().Be(17.99m);
        sub.GetProperty("priceChange").GetString().Should().Be("Increase");
        sub.GetProperty("previousAmount").GetDecimal().Should().Be(15.99m);
        overview.GetProperty("monthlyTotal").GetDecimal().Should().Be(17.99m);
        overview.GetProperty("yearlyTotal").GetDecimal().Should().Be(17.99m * 12m);

        using var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        (await other.GetFromJsonAsync<JsonElement>("/api/subscriptions")).GetProperty("subscriptions").GetArrayLength().Should().Be(0);
    }
}
