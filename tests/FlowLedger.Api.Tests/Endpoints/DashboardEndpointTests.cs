using System.Net;
using System.Net.Http.Json;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class DashboardEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    // The API reads the real clock for "this month", so the seeded dates must too: one read, shared by every test.

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Activity_separates_pending_from_posted_and_flags_large_new_merchant()
    {
        var acct = await (await _client.PostAsJsonAsync("/api/accounts",
            CreateAccountRequestBuilder.A().Named("Checking").StartingAt(0m).Build()))
            .Content.ReadFromJsonAsync<AccountDto>();
        var today = factory.ServerToday;

        async Task Post(decimal amount, string desc, DateOnly? posted) =>
            (await _client.PostAsJsonAsync("/api/transactions",
                CreateTransactionRequestBuilder.A().OnAccount(acct!.Id).WithAmount(amount).Described(desc).On(today).PostedOn(posted).Build()))
                .EnsureSuccessStatusCode();

        await Post(12m, "Coffee", today);
        await Post(30m, "Hold at Gas Station", null);
        await Post(900m, "Brand New Jeweler", today);

        var response = await _client.GetAsync("/api/dashboard/activity");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await response.Content.ReadFromJsonAsync<DashboardActivityDto>())!;

        dto.Pending.Should().ContainSingle(t => t.Description == "Hold at Gas Station");
        dto.RecentPosted.Select(t => t.Description).Should().Contain(["Coffee", "Brand New Jeweler"]);
        dto.Large.Should().ContainSingle(t => t.Description == "Brand New Jeweler");
        dto.Suspicious.Should().Contain(s => s.Transaction.Description == "Brand New Jeweler");
    }

    [Fact]
    public async Task Insights_are_cached_per_tenant_but_a_write_invalidates_them_at_once()
    {
        // A tenant of its own: nothing else cached for it, and other tests' data cannot leak into the zero-spend assertion.
        var mine = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var acct = (await (await mine.PostAsJsonAsync("/api/accounts",
            CreateAccountRequestBuilder.A().Named("Checking").OfType("Checking").StartingAt(0m).Build())).Content.ReadFromJsonAsync<AccountDto>())!;
        var today = factory.ServerToday;
        Task<HttpResponseMessage> Spend(decimal amount, string desc) => mine.PostAsJsonAsync("/api/transactions",
            CreateTransactionRequestBuilder.A().OnAccount(acct.Id).WithAmount(amount).WithDirection("Debit").Described(desc).On(today).PostedOn(today).AtMerchant(desc).Build());
        async Task<DashboardInsightsDto> Insights() =>
            (await (await mine.GetAsync("/api/dashboard/insights")).Content.ReadFromJsonAsync<DashboardInsightsDto>())!;

        decimal Spent(DashboardInsightsDto d) => d.SpendingByCategory.Sum(c => c.Amount);

        Spent(await Insights()).Should().Be(0m);
        await Spend(40m, "First Cafe");
        Spent(await Insights()).Should().Be(40m, "the save bumped the tenant's data version, so the cached empty answer is not served");

        await Spend(25m, "Second Cafe");
        Spent(await Insights()).Should().Be(65m);

        // Another tenant has its own cache entries and never sees these amounts.
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var theirs = (await (await other.GetAsync("/api/dashboard/insights")).Content.ReadFromJsonAsync<DashboardInsightsDto>())!;
        Spent(theirs).Should().Be(0m);
    }

    [Fact]
    public async Task Activity_requires_authentication()
    {
        using var anon = factory.CreateClient();
        (await anon.GetAsync("/api/dashboard/activity")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
