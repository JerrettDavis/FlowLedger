using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>Provider-agnostic multi-user: identity → tenant mapping, and hard data segmentation between tenants.</summary>
[Collection("ApiIntegration")]
public sealed class MultiUserTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private sealed record Resolved(Guid UserId, Guid TenantId, bool Created);

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Resolved> Resolve(string issuer, string subject, string? email = null)
    {
        var client = factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/users/resolve", new { issuer, subject, email, displayName = subject });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<Resolved>())!;
    }

    [Fact]
    public async Task Resolve_is_idempotent_per_issuer_and_subject_and_isolates_different_users()
    {
        var issuer = "https://idp.test/" + Guid.NewGuid();

        var alice = await Resolve(issuer, "alice");
        var aliceAgain = await Resolve(issuer, "alice");
        var bob = await Resolve(issuer, "bob");

        aliceAgain.TenantId.Should().Be(alice.TenantId);
        aliceAgain.Created.Should().BeFalse();
        bob.TenantId.Should().NotBe(alice.TenantId);
        bob.Created.Should().BeTrue();
    }

    [Fact]
    public async Task Same_subject_from_a_different_issuer_is_a_different_user()
    {
        var a = await Resolve("https://idp-a.test/" + Guid.NewGuid(), "same-sub");
        var b = await Resolve("https://idp-b.test/" + Guid.NewGuid(), "same-sub");

        b.TenantId.Should().NotBe(a.TenantId);
    }

    [Fact]
    public async Task Nobody_adopts_the_household_tenant_unless_an_owner_is_configured()
    {
        // No Tenancy:LegacyOwner* configured in the test host: even the very first user, claiming a verified email, must not get it.
        var client = factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/users/resolve",
            new { issuer = "https://idp.test/" + Guid.NewGuid(), subject = "first", email = "owner@example.com", emailVerified = true });
        var user = (await resp.Content.ReadFromJsonAsync<Resolved>())!;

        user.TenantId.Should().NotBe(FlowLedgerApiFactory.DemoTenantId);
    }

    [Fact]
    public async Task Resolve_requires_the_api_key()
    {
        var anonymous = factory.CreateClient();
        var resp = await anonymous.PostAsJsonAsync("/api/users/resolve", new { issuer = "x", subject = "y" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Data_created_by_one_tenant_is_invisible_to_another()
    {
        var issuer = "https://idp.test/" + Guid.NewGuid();
        var alice = await Resolve(issuer, "alice");
        var bob = await Resolve(issuer, "bob");
        var aliceClient = factory.CreateAuthenticatedClient(alice.TenantId);
        var bobClient = factory.CreateAuthenticatedClient(bob.TenantId);

        var created = await aliceClient.PostAsJsonAsync("/api/accounts", CreateAccountRequestBuilder.A().Named("Alice Checking").OfType("Checking").StartingAt(1234m).Build());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var account = (await created.Content.ReadFromJsonAsync<AccountDto>())!;

        var bobList = await bobClient.GetFromJsonAsync<List<AccountDto>>("/api/accounts");
        bobList.Should().NotContain(a => a.Id == account.Id);
        (await bobClient.GetAsync($"/api/accounts/{account.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var aliceList = await aliceClient.GetFromJsonAsync<List<AccountDto>>("/api/accounts");
        aliceList.Should().Contain(a => a.Id == account.Id);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("transactions")]
    [InlineData("assets")]
    [InlineData("recurring-flows")]
    [InlineData("categories")]
    public async Task Another_tenant_gets_404_by_id_and_never_sees_the_row_in_lists(string resource)
    {
        var issuer = "https://idp.test/" + Guid.NewGuid();
        var alice = factory.CreateAuthenticatedClient((await Resolve(issuer, "alice")).TenantId);
        var bob = factory.CreateAuthenticatedClient((await Resolve(issuer, "bob")).TenantId);

        var id = await SeedAsync(alice, resource);

        (await alice.GetAsync($"/api/{resource}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK, "the owner can read it");
        (await alice.GetStringAsync($"/api/{resource}")).Should().Contain(id.ToString());

        (await bob.GetAsync($"/api/{resource}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var bobList = await bob.GetAsync($"/api/{resource}");
        bobList.StatusCode.Should().Be(HttpStatusCode.OK);
        (await bobList.Content.ReadAsStringAsync()).Should().NotContain(id.ToString());
    }

    [Fact]
    public async Task Another_tenants_forecast_does_not_include_the_owners_accounts()
    {
        var issuer = "https://idp.test/" + Guid.NewGuid();
        var alice = factory.CreateAuthenticatedClient((await Resolve(issuer, "alice")).TenantId);
        var bob = factory.CreateAuthenticatedClient((await Resolve(issuer, "bob")).TenantId);

        var accountId = await SeedAsync(alice, "accounts");
        await SeedAsync(alice, "recurring-flows", accountId);

        (await alice.GetStringAsync("/api/forecast")).Should().Contain(accountId.ToString());

        var bobForecast = await bob.GetAsync("/api/forecast");
        (await bobForecast.Content.ReadAsStringAsync()).Should().NotContain(accountId.ToString());
        // Explicitly requesting the other tenant's account id must not leak it either.
        var targeted = await bob.GetAsync($"/api/forecast?accounts={accountId}");
        (await targeted.Content.ReadAsStringAsync()).Should().NotContain("Alice Checking");
    }

    private static async Task<Guid> SeedAsync(HttpClient c, string resource, Guid? accountId = null)
    {
        async Task<Guid> Post(string url, object body)
        {
            var r = await c.PostAsJsonAsync(url, body);
            r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
            var json = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            return json.GetProperty("id").GetGuid();
        }

        if (resource == "accounts")
        {
            return await Post("/api/accounts", CreateAccountRequestBuilder.A().Named("Alice Checking").OfType("Checking").StartingAt(1234m).Build());
        }

        var account = accountId ?? await SeedAsync(c, "accounts");
        return resource switch
        {
            "transactions" => await Post("/api/transactions",
                CreateTransactionRequestBuilder.A().OnAccount(account).WithAmount(50m).WithDirection("Debit").Described("Coffee Shop").On(new DateOnly(2026, 1, 15)).Build()),
            "assets" => await Post("/api/assets",
                new CreateAssetRequest("MacBook", "Electronics", 2000m, "USD", null, new DateOnly(2024, 1, 1), 2400m, "Apple", "MBP", null, null,
                    "Manual", null, null, null, null, null)),
            "recurring-flows" => await Post("/api/recurring-flows",
                new CreateRecurringFlowRequest(account, "Rent", 1200m, "USD", "Debit", "Fixed", "Monthly", 1, null, null, null,
                    new DateOnly(2026, 1, 1), null, null, "Landlord")),
            "categories" => await Post("/api/categories", CreateCategoryRequestBuilder.A().AtPath("food").Named("Food").Build()),
            _ => throw new ArgumentOutOfRangeException(nameof(resource)),
        };
    }
}
