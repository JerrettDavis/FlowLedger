using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#189: a tenant owner erases their OWN tenant after typing the confirmation; nobody can use the route to touch another tenant, and the household tenant is protected.</summary>
[Collection("ApiIntegration")]
public sealed class SelfServiceErasureTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private const string Route = "/api/account/delete-my-data";

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ResolveBody(Guid TenantId);

    private async Task<Guid> NewUserTenantAsync()
    {
        var resp = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/users/resolve",
            new { issuer = "https://idp.test/" + Guid.NewGuid(), subject = "person", email = (string?)null, displayName = "x" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ResolveBody>())!.TenantId;
    }

    private static Task<HttpResponseMessage> Delete(HttpClient client, string? confirmation) =>
        client.PostAsJsonAsync(Route, new { confirmation });

    private static async Task AddAccountAsync(HttpClient client) =>
        (await client.PostAsJsonAsync("/api/accounts", new FlowLedger.Contracts.CreateAccountRequest("Mine", "Checking", 10m, "USD", null, null)))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);

    [Fact]
    public async Task Without_the_typed_phrase_nothing_is_deleted()
    {
        var owner = factory.CreateAuthenticatedClient(await NewUserTenantAsync());
        await AddAccountAsync(owner);

        (await Delete(owner, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Delete(owner, "delete my data")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsync(Route, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await owner.GetFromJsonAsync<List<object>>("/api/accounts")).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_owner_erases_their_own_tenant_and_only_their_own()
    {
        var ownerTenant = await NewUserTenantAsync();
        var bystanderTenant = await NewUserTenantAsync();
        var owner = factory.CreateAuthenticatedClient(ownerTenant);
        var bystander = factory.CreateAuthenticatedClient(bystanderTenant);
        await AddAccountAsync(owner);
        await AddAccountAsync(bystander);

        var resp = await Delete(owner, "DELETE MY DATA");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("tenantId").GetGuid().Should().Be(ownerTenant);
        body.RootElement.GetProperty("totalRows").GetInt32().Should().BeGreaterThan(0);
        body.RootElement.GetProperty("providers").ValueKind.Should().Be(JsonValueKind.Array);
        (await owner.GetFromJsonAsync<List<object>>("/api/accounts")).Should().BeEmpty();
        (await owner.GetFromJsonAsync<List<object>>("/api/users/identities")).Should().BeEmpty("the owner's login mapping is erased with the tenant");
        (await bystander.GetFromJsonAsync<List<object>>("/api/accounts")).Should().HaveCount(1, "another tenant is never touched");

        // The household admin sees the tenant as deleted in the registry (the audit record of the erasure).
        var household = factory.CreateAuthenticatedClient(FlowLedgerApiFactory.DemoTenantId);
        var deleted = await household.GetFromJsonAsync<List<JsonElement>>("/api/data/tenants?status=deleted");
        deleted!.Select(t => t.GetProperty("tenantId").GetGuid()).Should().Contain(ownerTenant);
    }

    [Fact]
    public async Task The_household_tenant_cannot_delete_itself_through_the_self_service_route()
    {
        var household = factory.CreateAuthenticatedClient(FlowLedgerApiFactory.DemoTenantId);
        await AddAccountAsync(household);

        var resp = await Delete(household, "DELETE MY DATA");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await household.GetFromJsonAsync<List<object>>("/api/accounts")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_route_is_open_to_any_tenant_owner_and_does_not_need_the_household_admin_token()
    {
        // A non-household tenant reaches the handler (400 for the missing phrase), not the household/admin gate (403).
        var owner = factory.CreateAuthenticatedClient(await NewUserTenantAsync());

        (await Delete(owner, null)).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
