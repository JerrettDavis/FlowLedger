using System.Net;
using System.Net.Http.Json;
using FlowLedger.Api.Endpoints;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#157: transactions, categories and recurring flows carry an ETag and honour If-Match (409 ProblemDetails when stale).</summary>
[Collection("ApiIntegration")]
public sealed class RowVersionEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, string? ifMatch)
    {
        var message = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await _client.SendAsync(message);
    }

    private async Task<AccountDto> CreateAccount()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", CreateAccountRequestBuilder.A().Named("Versioned").OfType("Checking").StartingAt(0m).Build());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task<TransactionDto> CreateTransaction()
    {
        var account = await CreateAccount();
        var response = await _client.PostAsJsonAsync("/api/transactions", CreateTransactionRequestBuilder.A().OnAccount(account.Id).WithAmount(25m).WithDirection("Debit").Described("Corner shop").On(new DateOnly(2026, 2, 3)).Build());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TransactionDto>())!;
    }

    private async Task<CategoryDto> CreateCategory(string path)
    {
        var response = await _client.PostAsJsonAsync("/api/categories", CreateCategoryRequestBuilder.A().AtPath(path).Build());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private async Task<RecurringFlowDto> CreateFlow()
    {
        var account = await CreateAccount();
        var response = await _client.PostAsJsonAsync("/api/recurring-flows", new CreateRecurringFlowRequest(
            account.Id, "Rent", 1200m, "USD", "Debit", "Fixed", "Monthly", 1, null, null, null, new DateOnly(2026, 1, 1), null, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecurringFlowDto>())!;
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_transaction_get_carries_an_etag_matching_its_version()
    {
        var created = await CreateTransaction();

        var response = await _client.GetAsync($"/api/transactions/{created.Id}");

        var dto = await response.Content.ReadFromJsonAsync<TransactionDto>();
        dto!.Version.Should().NotBe(0u);
        response.Headers.ETag!.Tag.Should().Be($"\"{dto.Version}\"");
    }

    [Fact]
    public async Task A_stale_if_match_on_a_transaction_note_returns_409_and_does_not_overwrite()
    {
        var created = await CreateTransaction();
        var url = $"/api/transactions/{created.Id}/note";
        (await SendAsync(HttpMethod.Put, url, new { note = "first" }, $"\"{created.Version}\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stale = await SendAsync(HttpMethod.Put, url, new { note = "second" }, $"\"{created.Version}\"");

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        stale.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await _client.GetFromJsonAsync<TransactionDto>($"/api/transactions/{created.Id}"))!.Notes.Should().Be("first");
    }

    [Fact]
    public async Task A_current_if_match_on_a_transaction_category_change_succeeds_and_a_stale_one_conflicts()
    {
        var created = await CreateTransaction();
        var category = await CreateCategory("groceries");
        var url = $"/api/transactions/{created.Id}/category";

        (await SendAsync(HttpMethod.Put, url, new { categoryId = category.Id }, $"\"{created.Version}\"")).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = await SendAsync(HttpMethod.Put, url, new { categoryId = category.Id }, $"\"{created.Version}\"");

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_stale_if_match_on_transaction_splits_returns_409_even_though_splits_live_in_their_own_table()
    {
        var created = await CreateTransaction();
        var category = await CreateCategory("household");
        var splitsUrl = $"/api/transactions/{created.Id}/splits";
        (await SendAsync(HttpMethod.Put, $"/api/transactions/{created.Id}/note", new { note = "moves the version" }, null)).EnsureSuccessStatusCode();

        var response = await SendAsync(HttpMethod.Put, splitsUrl,
            new { splits = new[] { new { amount = 10m, categoryId = category.Id, notes = (string?)null }, new { amount = 15m, categoryId = category.Id, notes = (string?)null } } },
            $"\"{created.Version}\"");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.GetFromJsonAsync<TransactionDto>($"/api/transactions/{created.Id}"))!.Splits.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task A_current_if_match_on_transaction_splits_succeeds()
    {
        var created = await CreateTransaction();
        var category = await CreateCategory("household2");

        var response = await SendAsync(HttpMethod.Put, $"/api/transactions/{created.Id}/splits",
            new { splits = new[] { new { amount = 10m, categoryId = category.Id, notes = (string?)null }, new { amount = 15m, categoryId = category.Id, notes = (string?)null } } },
            $"\"{created.Version}\"");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetFromJsonAsync<TransactionDto>($"/api/transactions/{created.Id}"))!.Splits.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_transaction_write_without_if_match_stays_last_write_wins_and_a_malformed_one_is_400()
    {
        var created = await CreateTransaction();
        var url = $"/api/transactions/{created.Id}/note";
        (await SendAsync(HttpMethod.Put, url, new { note = "one" }, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Put, url, new { note = "two" }, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SendAsync(HttpMethod.Put, url, new { note = "three" }, "\"nope\"")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_transaction_write_returns_the_new_etag_so_the_next_write_can_chain_without_rereading()
    {
        var created = await CreateTransaction();
        var url = $"/api/transactions/{created.Id}/note";

        var first = await SendAsync(HttpMethod.Put, url, new { note = "one" }, $"\"{created.Version}\"");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var chained = first.Headers.ETag!.Tag;
        chained.Should().NotBe($"\"{created.Version}\"");
        (await _client.GetAsync($"/api/transactions/{created.Id}")).Headers.ETag!.Tag.Should().Be(chained);

        (await SendAsync(HttpMethod.Put, url, new { note = "two" }, chained)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Categories ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_stale_if_match_on_a_category_rename_returns_409_and_on_delete_too()
    {
        var created = await CreateCategory("travel");
        var url = $"/api/categories/{created.Id}";
        var renamed = await SendAsync(HttpMethod.Put, url, new UpdateCategoryRequest("travel-2", "Travel 2"), $"\"{created.Version}\"");
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await renamed.Content.ReadFromJsonAsync<CategoryDto>();
        renamed.Headers.ETag!.Tag.Should().Be($"\"{dto!.Version}\"");

        (await SendAsync(HttpMethod.Put, url, new UpdateCategoryRequest("travel-3", "Travel 3"), $"\"{created.Version}\"")).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Delete, url, null, $"\"{created.Version}\"")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await SendAsync(HttpMethod.Delete, url, null, $"\"{dto.Version}\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Recurring flows ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_stale_if_match_on_a_recurring_flow_update_and_delete_returns_409()
    {
        var created = await CreateFlow();
        var url = $"/api/recurring-flows/{created.Id}";
        var updated = await SendAsync(HttpMethod.Put, url, new UpdateRecurringFlowRequest(1300m, "Fixed"), $"\"{created.Version}\"");
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await updated.Content.ReadFromJsonAsync<RecurringFlowDto>();
        dto!.Version.Should().NotBe(created.Version);

        (await SendAsync(HttpMethod.Put, url, new UpdateRecurringFlowRequest(1400m, "Fixed"), $"\"{created.Version}\"")).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Delete, url, null, $"\"{created.Version}\"")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.GetFromJsonAsync<RecurringFlowDto>(url))!.Amount.Should().Be(1300m);

        (await SendAsync(HttpMethod.Delete, url, null, $"\"{dto.Version}\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
