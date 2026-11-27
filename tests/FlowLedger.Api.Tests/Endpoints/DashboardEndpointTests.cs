using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class DashboardEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
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
    public async Task Activity_requires_authentication()
    {
        using var anon = factory.CreateClient();
        (await anon.GetAsync("/api/dashboard/activity")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
