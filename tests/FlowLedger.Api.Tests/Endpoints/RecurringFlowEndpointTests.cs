using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.RecurringFlows;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class RecurringFlowEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CreateRecurringFlowRequest ValidRequest(Guid accountId, string name = "Rent") =>
        new(
            AccountId: accountId,
            Name: name,
            Amount: 1200m,
            Currency: "USD",
            Direction: "Debit",
            AmountModel: "Fixed",
            RecurrenceFrequency: "Monthly",
            DayOfMonth: 1,
            SecondDayOfMonth: null,
            IntervalWeeks: null,
            AnchorDayOfWeek: null,
            StartDate: new DateOnly(2026, 1, 1),
            EndDate: null,
            CategoryId: null,
            Counterparty: "Landlord");

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_creates_recurring_flow_and_returns_201()
    {
        var account = await CreateAccount();
        var request = ValidRequest(account.Id);

        var response = await _client.PostAsJsonAsync("/api/recurring-flows", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<RecurringFlowDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Rent");
        dto.Amount.Should().Be(1200m);
        dto.Direction.Should().Be("Debit");
        dto.RecurrenceFrequency.Should().Be("Monthly");
        dto.IsActive.Should().BeTrue();
        response.Headers.Location.Should().NotBeNull();
    }

    // ── Get by id ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_by_id_returns_200_for_existing_flow()
    {
        var account = await CreateAccount();
        var flow = await CreateFlow(account.Id);

        var response = await _client.GetAsync($"/api/recurring-flows/{flow.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<RecurringFlowDto>();
        dto!.Id.Should().Be(flow.Id);
    }

    [Fact]
    public async Task Get_by_id_returns_404_for_missing_flow()
    {
        var response = await _client.GetAsync($"/api/recurring-flows/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_list_returns_200_and_includes_created_flow()
    {
        var account = await CreateAccount();
        var flow = await CreateFlow(account.Id, "Monthly Salary");

        var response = await _client.GetAsync("/api/recurring-flows");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<RecurringFlowDto>>();
        list.Should().NotBeNull();
        list!.Should().Contain(f => f.Id == flow.Id);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_updates_flow_amount_and_returns_200()
    {
        var account = await CreateAccount();
        var flow = await CreateFlow(account.Id);

        var response = await _client.PutAsJsonAsync(
            $"/api/recurring-flows/{flow.Id}",
            new UpdateRecurringFlowRequest(1500m, "Fixed"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<RecurringFlowDto>();
        dto!.Amount.Should().Be(1500m);
    }

    [Fact]
    public async Task Put_returns_404_for_missing_flow()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/recurring-flows/{Guid.NewGuid()}",
            new UpdateRecurringFlowRequest(100m, "Fixed"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_returns_204_for_existing_flow()
    {
        var account = await CreateAccount();
        var flow = await CreateFlow(account.Id);

        var response = await _client.DeleteAsync($"/api/recurring-flows/{flow.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_returns_404_for_missing_flow()
    {
        var response = await _client.DeleteAsync($"/api/recurring-flows/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_with_zero_amount_returns_400()
    {
        var account = await CreateAccount();
        var request = ValidRequest(account.Id) with { Amount = 0m };

        var response = await _client.PostAsJsonAsync("/api/recurring-flows", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_invalid_direction_returns_400()
    {
        var account = await CreateAccount();
        var request = ValidRequest(account.Id) with { Direction = "Sideways" };

        var response = await _client.PostAsJsonAsync("/api/recurring-flows", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_invalid_frequency_returns_400()
    {
        var account = await CreateAccount();
        var request = ValidRequest(account.Id) with { RecurrenceFrequency = "Quarterly" };

        var response = await _client.PostAsJsonAsync("/api/recurring-flows", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_invalid_currency_returns_400()
    {
        var account = await CreateAccount();
        var request = ValidRequest(account.Id) with { Currency = "US" };

        var response = await _client.PostAsJsonAsync("/api/recurring-flows", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Flow_is_not_visible_to_different_tenant()
    {
        var account = await CreateAccount();
        var flow = await CreateFlow(account.Id);

        using var otherClient = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var listResponse = await otherClient.GetAsync("/api/recurring-flows");
        var list = await listResponse.Content.ReadFromJsonAsync<List<RecurringFlowDto>>();

        list.Should().NotContain(f => f.Id == flow.Id);

        var getResponse = await otherClient.GetAsync($"/api/recurring-flows/{flow.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<AccountDto> CreateAccount(string name = "Test Account")
    {
        var response = await _client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest(name, "Checking", 0m, "USD", null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task<RecurringFlowDto> CreateFlow(Guid accountId, string name = "Rent")
    {
        var response = await _client.PostAsJsonAsync("/api/recurring-flows", ValidRequest(accountId, name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecurringFlowDto>())!;
    }
}
