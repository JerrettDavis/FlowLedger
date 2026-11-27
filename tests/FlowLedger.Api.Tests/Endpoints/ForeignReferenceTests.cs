using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#146: ids in request bodies must belong to the caller's tenant.</summary>
[Collection("ApiIntegration")]
public sealed class ForeignReferenceTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    public async Task InitializeAsync() => await factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> CreateAccountAsync(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/accounts", new { name = "Checking", accountType = "Checking", currency = "USD", openingBalance = 100m });
        r.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await r.Content.ReadFromJsonAsync<AccountDto>())!.Id;
    }

    private static object NewTx(Guid accountId, Guid? categoryId = null) => new
    {
        accountId,
        amount = 5m,
        currency = "USD",
        direction = "Debit",
        description = "Coffee",
        effectiveDate = "2026-06-01",
        postedDate = "2026-06-01",
        categoryId,
    };

    [Fact]
    public async Task Create_transaction_with_another_tenants_account_is_rejected_and_not_persisted()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var a = factory.CreateAuthenticatedClient(tenantA);
        using var b = factory.CreateAuthenticatedClient(tenantB);
        var accountOfB = await CreateAccountAsync(b);

        var response = await a.PostAsJsonAsync("/api/transactions", NewTx(accountOfB));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
        var list = await b.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/transactions?accountId={accountOfB}");
        list.ToString().Should().NotContain("Coffee");
    }

    [Fact]
    public async Task Create_transaction_with_nonexistent_category_is_rejected()
    {
        var a = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var account = await CreateAccountAsync(a);

        var response = await a.PostAsJsonAsync("/api/transactions", NewTx(account, Guid.NewGuid()));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_transaction_with_own_account_still_works()
    {
        var a = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var account = await CreateAccountAsync(a);

        var response = await a.PostAsJsonAsync("/api/transactions", NewTx(account));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_recurring_flow_with_another_tenants_account_is_rejected()
    {
        using var a = factory.CreateAuthenticatedClient(Guid.NewGuid());
        using var b = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var accountOfB = await CreateAccountAsync(b);

        var response = await a.PostAsJsonAsync("/api/recurring-flows", new
        {
            accountId = accountOfB,
            name = "Rent",
            amount = 10m,
            currency = "USD",
            direction = "Debit",
            amountModel = "Fixed",
            recurrenceFrequency = "Monthly",
            dayOfMonth = 1,
            startDate = "2026-01-01",
        });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }
}
