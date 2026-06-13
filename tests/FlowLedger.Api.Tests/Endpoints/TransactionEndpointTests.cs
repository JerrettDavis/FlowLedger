using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class TransactionEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_creates_transaction_and_returns_201()
    {
        var account = await CreateAccount();
        var request = ValidRequest(account.Id);

        var response = await _client.PostAsJsonAsync("/api/transactions", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<TransactionDto>();
        dto.Should().NotBeNull();
        dto!.AccountId.Should().Be(account.Id);
        dto.Amount.Should().Be(50m);
        dto.Direction.Should().Be("Debit");
        dto.Description.Should().Be("Coffee Shop");
        response.Headers.Location.Should().NotBeNull();
    }

    // ── Get by id ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_by_id_returns_200_for_existing_transaction()
    {
        var account = await CreateAccount();
        var tx = await CreateTransaction(account.Id);

        var response = await _client.GetAsync($"/api/transactions/{tx.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<TransactionDto>();
        dto!.Id.Should().Be(tx.Id);
    }

    [Fact]
    public async Task Get_by_id_returns_404_for_missing_transaction()
    {
        var response = await _client.GetAsync($"/api/transactions/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_list_returns_200_and_includes_created_transaction()
    {
        var account = await CreateAccount();
        var tx = await CreateTransaction(account.Id);

        var response = await _client.GetAsync("/api/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<TransactionDto>>();
        list.Should().NotBeNull();
        list!.Should().Contain(t => t.Id == tx.Id);
    }

    [Fact]
    public async Task Get_list_filtered_by_account_id_returns_only_that_accounts_transactions()
    {
        var account1 = await CreateAccount("Account 1");
        var account2 = await CreateAccount("Account 2");
        var tx1 = await CreateTransaction(account1.Id);
        var tx2 = await CreateTransaction(account2.Id);

        var response = await _client.GetAsync($"/api/transactions?AccountId={account1.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<TransactionDto>>();
        list!.Should().Contain(t => t.Id == tx1.Id);
        list.Should().NotContain(t => t.Id == tx2.Id);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_with_zero_amount_returns_400()
    {
        var account = await CreateAccount();
        var request = new CreateTransactionRequest(
            account.Id, 0m, "USD", "Debit", "Test",
            new DateOnly(2026, 1, 15), null, null, null, null);

        var response = await _client.PostAsJsonAsync("/api/transactions", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_invalid_direction_returns_400()
    {
        var account = await CreateAccount();
        var request = new CreateTransactionRequest(
            account.Id, 10m, "USD", "BadDirection", "Test",
            new DateOnly(2026, 1, 15), null, null, null, null);

        var response = await _client.PostAsJsonAsync("/api/transactions", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_invalid_currency_returns_400()
    {
        var account = await CreateAccount();
        var request = new CreateTransactionRequest(
            account.Id, 10m, "US", "Debit", "Test",
            new DateOnly(2026, 1, 15), null, null, null, null);

        var response = await _client.PostAsJsonAsync("/api/transactions", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Transaction_is_not_visible_to_different_tenant()
    {
        var account = await CreateAccount();
        var tx = await CreateTransaction(account.Id);

        using var otherClient = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var listResponse = await otherClient.GetAsync("/api/transactions");
        var list = await listResponse.Content.ReadFromJsonAsync<List<TransactionDto>>();

        list.Should().NotContain(t => t.Id == tx.Id);

        var getResponse = await otherClient.GetAsync($"/api/transactions/{tx.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static CreateTransactionRequest ValidRequest(Guid accountId) =>
        new(accountId, 50m, "USD", "Debit", "Coffee Shop",
            new DateOnly(2026, 1, 15), null, null, null, null);

    private async Task<AccountDto> CreateAccount(string name = "Test Account")
    {
        var response = await _client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest(name, "Checking", 0m, "USD", null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task<TransactionDto> CreateTransaction(Guid accountId)
    {
        var response = await _client.PostAsJsonAsync("/api/transactions", ValidRequest(accountId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TransactionDto>())!;
    }
}
