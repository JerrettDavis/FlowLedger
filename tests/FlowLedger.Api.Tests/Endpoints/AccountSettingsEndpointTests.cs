using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class AccountSettingsEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<AccountDto> CreateAccountAsync(string name, string type)
    {
        var r = await _client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(name, type, 100m, "USD", null, null));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task AddTxAsync(Guid accountId, string description)
    {
        var r = await _client.PostAsJsonAsync("/api/transactions", new CreateTransactionRequest(
            accountId, 12.34m, "USD", "Debit", description, new DateOnly(2026, 11, 10), null, null, null, null));
        r.EnsureSuccessStatusCode();
    }

    private async Task<List<TransactionDto>> ListAsync(string query = "")
        => (await _client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions" + query))!;

    [Fact]
    public async Task New_investment_account_is_a_holding()
    {
        var inv = await CreateAccountAsync("Brokerage", "Investment");
        inv.Mode.Should().Be("Holding");
        inv.HideTransactions.Should().BeFalse();
    }

    [Fact]
    public async Task Holding_transactions_are_left_out_of_the_default_list_but_shown_when_filtered_or_included()
    {
        var chk = await CreateAccountAsync("Checking", "Checking");
        var inv = await CreateAccountAsync("Brokerage", "Investment");
        await AddTxAsync(chk.Id, "Coffee");
        await AddTxAsync(inv.Id, "Buy VTI");

        (await ListAsync()).Select(t => t.Description).Should().Equal("Coffee");
        (await ListAsync($"?accountId={inv.Id}")).Select(t => t.Description).Should().Equal("Buy VTI");
        (await ListAsync("?includeHidden=true")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Hide_flag_on_a_standard_account_hides_its_transactions_until_reverted()
    {
        var chk = await CreateAccountAsync("Checking", "Checking");
        await AddTxAsync(chk.Id, "Coffee");

        var put = await _client.PutAsJsonAsync($"/api/accounts/{chk.Id}/settings", new AccountSettingsRequest(true, "Standard", true));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListAsync()).Should().BeEmpty();

        await _client.PutAsJsonAsync($"/api/accounts/{chk.Id}/settings", new AccountSettingsRequest(true, "Standard", false));
        (await ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Disabled_account_vanishes_and_can_be_reenabled()
    {
        var chk = await CreateAccountAsync("Checking", "Checking");
        await AddTxAsync(chk.Id, "Coffee");

        await _client.PutAsJsonAsync($"/api/accounts/{chk.Id}/settings", new AccountSettingsRequest(false, "Standard", false));

        (await _client.GetFromJsonAsync<List<AccountDto>>("/api/accounts"))!.Should().BeEmpty();
        (await _client.GetFromJsonAsync<List<AccountDto>>("/api/accounts?includeInactive=true"))!.Should().ContainSingle(a => !a.IsActive);
        (await ListAsync()).Should().BeEmpty();

        await _client.PutAsJsonAsync($"/api/accounts/{chk.Id}/settings", new AccountSettingsRequest(true, "Standard", false));
        (await ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Invalid_mode_is_rejected_and_unknown_account_is_404()
    {
        var chk = await CreateAccountAsync("Checking", "Checking");
        (await _client.PutAsJsonAsync($"/api/accounts/{chk.Id}/settings", new AccountSettingsRequest(true, "Bogus", false)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/accounts/{Guid.NewGuid()}/settings", new AccountSettingsRequest(true, "Standard", false)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
