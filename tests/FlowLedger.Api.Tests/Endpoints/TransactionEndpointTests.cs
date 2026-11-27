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
    public async Task Get_list_reports_the_total_matching_count_independent_of_paging()
    {
        var account = await CreateAccount();
        await CreateTransaction(account.Id);
        await CreateTransaction(account.Id);
        await CreateTransaction(account.Id);

        var response = await _client.GetAsync($"/api/transactions?accountId={account.Id}&take=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<TransactionDto>>()).Should().HaveCount(2);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("3");
    }

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

    // ── Export ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_csv_streams_every_matching_row_in_list_order_and_equals_the_csv_built_from_the_list()
    {
        var account = await CreateAccount();
        // Paging beyond one export page of 500 rows, and the keyset tie-breaks, are covered against the real store in Infrastructure.Tests.
        for (var i = 0; i < 12; i++)
        {
            var request = new CreateTransactionRequest(account.Id, 10m + i, "USD", i % 3 == 0 ? "Credit" : "Debit", $"Row {i}, \"quoted\"",
                new DateOnly(2026, 1, 10 + (i % 4)), null, null, null, null);
            (await _client.PostAsJsonAsync("/api/transactions", request)).EnsureSuccessStatusCode();
        }

        var response = await _client.GetAsync("/api/transactions/export.csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.Should().StartWith("flowledger-transactions-");
        var listed = (await _client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions?take=100"))!;
        listed.Should().HaveCount(12);
        (await response.Content.ReadAsStringAsync()).Should().Be(TransactionCsv.Build(listed));
    }

    [Fact]
    public async Task Export_csv_honours_the_list_filters_and_order()
    {
        var account = await CreateAccount();
        for (var i = 0; i < 6; i++)
        {
            (await _client.PostAsJsonAsync("/api/transactions", new CreateTransactionRequest(
                account.Id, 5m + i, "USD", "Debit", i < 3 ? $"Coffee {i}" : $"Rent {i}", new DateOnly(2026, 2, 1 + i), null, null, null, null))).EnsureSuccessStatusCode();
        }

        var csv = await _client.GetStringAsync("/api/transactions/export.csv?search=coffee");

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1 + 3, "the header and the three coffee rows");
        lines[1].Should().Contain("Coffee 2", "newest first by default");
    }

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
