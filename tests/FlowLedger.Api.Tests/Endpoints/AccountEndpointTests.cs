using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class AccountEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CreateAccountRequest ValidRequest(string name = "Test Checking") =>
        new(name, "Checking", 0m, "USD", null, null);

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_creates_account_and_returns_201()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", ValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<AccountDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Test Checking");
        dto.AccountType.Should().Be("Checking");
        dto.BalanceCurrency.Should().Be("USD");
        dto.IsActive.Should().BeTrue();
        response.Headers.Location.Should().NotBeNull();
    }

    // ── Get by id ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_by_id_returns_200_for_existing_account()
    {
        var created = await CreateAccount();

        var response = await _client.GetAsync($"/api/accounts/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<AccountDto>();
        dto!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Get_by_id_returns_404_for_missing_account()
    {
        var response = await _client.GetAsync($"/api/accounts/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_list_returns_200_and_includes_created_account()
    {
        var created = await CreateAccount("My Savings");

        var response = await _client.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<AccountDto>>();
        list.Should().NotBeNull();
        list!.Should().Contain(a => a.Id == created.Id);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_updates_account_name_and_returns_200()
    {
        var created = await CreateAccount();

        var response = await _client.PutAsJsonAsync(
            $"/api/accounts/{created.Id}",
            new UpdateAccountRequest("Renamed Account"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<AccountDto>();
        dto!.Name.Should().Be("Renamed Account");
    }

    [Fact]
    public async Task Put_returns_404_for_missing_account()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/accounts/{Guid.NewGuid()}",
            new UpdateAccountRequest("Whatever"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_returns_204_for_existing_account()
    {
        var created = await CreateAccount();

        var response = await _client.DeleteAsync($"/api/accounts/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_returns_404_for_missing_account()
    {
        var response = await _client.DeleteAsync($"/api/accounts/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_with_invalid_account_type_returns_400()
    {
        var request = new CreateAccountRequest("Bad", "NotARealType", 0m, "USD", null, null);

        var response = await _client.PostAsJsonAsync("/api/accounts", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_invalid_currency_length_returns_400()
    {
        var request = new CreateAccountRequest("Bad", "Checking", 0m, "US", null, null);

        var response = await _client.PostAsJsonAsync("/api/accounts", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Account_is_not_visible_to_different_tenant()
    {
        var created = await CreateAccount();

        using var otherClient = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var listResponse = await otherClient.GetAsync("/api/accounts");
        var list = await listResponse.Content.ReadFromJsonAsync<List<AccountDto>>();

        list.Should().NotContain(a => a.Id == created.Id);

        var getResponse = await otherClient.GetAsync($"/api/accounts/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<AccountDto> CreateAccount(string name = "Test Account")
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", ValidRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }
}
