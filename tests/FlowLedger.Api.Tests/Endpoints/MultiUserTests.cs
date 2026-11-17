using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
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

        var created = await aliceClient.PostAsJsonAsync("/api/accounts", new CreateAccountRequest("Alice Checking", "Checking", 1234m, "USD", null, null));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var account = (await created.Content.ReadFromJsonAsync<AccountDto>())!;

        var bobList = await bobClient.GetFromJsonAsync<List<AccountDto>>("/api/accounts");
        bobList.Should().NotContain(a => a.Id == account.Id);
        (await bobClient.GetAsync($"/api/accounts/{account.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var aliceList = await aliceClient.GetFromJsonAsync<List<AccountDto>>("/api/accounts");
        aliceList.Should().Contain(a => a.Id == account.Id);
    }
}
