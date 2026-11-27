using System.Net;
using System.Net.Http.Json;
using FlowLedger.TestSupport.Builders;
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

    // ── Optimistic concurrency (#157) ─────────────────────────────────────────

    private Task<HttpResponseMessage> PutRename(Guid id, string name, string? ifMatch)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, $"/api/accounts/{id}") { Content = JsonContent.Create(new UpdateAccountRequest(name)) };
        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return _client.SendAsync(message);
    }

    [Fact]
    public async Task Get_returns_an_etag_matching_the_dto_version()
    {
        var created = await CreateAccount();

        var response = await _client.GetAsync($"/api/accounts/{created.Id}");

        var dto = await response.Content.ReadFromJsonAsync<AccountDto>();
        dto!.Version.Should().NotBe(0u);
        response.Headers.ETag!.Tag.Should().Be($"\"{dto.Version}\"");
    }

    [Fact]
    public async Task Put_with_current_if_match_succeeds_and_returns_a_new_etag()
    {
        var created = await CreateAccount();

        var response = await PutRename(created.Id, "Renamed", $"\"{created.Version}\"");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<AccountDto>();
        dto!.Version.Should().NotBe(created.Version);
        response.Headers.ETag!.Tag.Should().Be($"\"{dto.Version}\"");
    }

    [Fact]
    public async Task Put_with_stale_if_match_returns_409_problem_and_does_not_overwrite()
    {
        var created = await CreateAccount();
        (await PutRename(created.Id, "First writer", $"\"{created.Version}\"")).EnsureSuccessStatusCode();

        // A second writer still holding the original version loses.
        var response = await PutRename(created.Id, "Second writer", $"\"{created.Version}\"");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var fetched = await _client.GetFromJsonAsync<AccountDto>($"/api/accounts/{created.Id}");
        fetched!.Name.Should().Be("First writer");
    }

    [Fact]
    public async Task Put_without_if_match_stays_last_write_wins()
    {
        var created = await CreateAccount();
        (await PutRename(created.Id, "First", $"\"{created.Version}\"")).EnsureSuccessStatusCode();

        var response = await PutRename(created.Id, "Second", ifMatch: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Put_with_malformed_if_match_returns_400()
    {
        var created = await CreateAccount();

        var response = await PutRename(created.Id, "Renamed", "\"not-a-version\"");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_settings_with_stale_if_match_returns_409()
    {
        var created = await CreateAccount();
        (await PutRename(created.Id, "Moves the version", null)).EnsureSuccessStatusCode();

        var message = new HttpRequestMessage(HttpMethod.Put, $"/api/accounts/{created.Id}/settings")
        {
            Content = JsonContent.Create(AccountSettingsRequestBuilder.A().Active(false).InMode("Standard").Build()),
        };
        message.Headers.TryAddWithoutValidation("If-Match", $"\"{created.Version}\"");
        var response = await _client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Change type (manual override) ─────────────────────────────────────────

    [Fact]
    public async Task Put_type_changes_the_type_and_pins_it()
    {
        var created = await CreateAccount();

        var response = await _client.PutAsJsonAsync(
            $"/api/accounts/{created.Id}/type", new ChangeAccountTypeRequest("CreditCard"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<AccountDto>();
        dto!.AccountType.Should().Be("CreditCard");
        dto.TypeLockedByUser.Should().BeTrue();

        var fetched = await _client.GetFromJsonAsync<AccountDto>($"/api/accounts/{created.Id}");
        fetched!.AccountType.Should().Be("CreditCard");
        fetched.TypeLockedByUser.Should().BeTrue();
    }

    [Fact]
    public async Task Put_type_with_unknown_type_returns_400()
    {
        var created = await CreateAccount();

        var response = await _client.PutAsJsonAsync(
            $"/api/accounts/{created.Id}/type", new ChangeAccountTypeRequest("NotARealType"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_type_returns_404_for_missing_account()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/accounts/{Guid.NewGuid()}/type", new ChangeAccountTypeRequest("Savings"));

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
        var request = CreateAccountRequestBuilder.A().Named("Bad").OfType("NotARealType").StartingAt(0m).Build();

        var response = await _client.PostAsJsonAsync("/api/accounts", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_invalid_currency_length_returns_400()
    {
        var request = CreateAccountRequestBuilder.A().Named("Bad").OfType("Checking").StartingAt(0m).InCurrency("US").Build();

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
