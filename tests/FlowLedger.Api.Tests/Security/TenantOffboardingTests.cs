using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#189 / #169: the household admin can offboard (erase) another tenant; nobody else can, and the household tenant is protected.</summary>
[Collection("ApiIntegration")]
public sealed class TenantOffboardingTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ResolveBody(Guid TenantId);

    private async Task<Guid> NewUserTenantAsync()
    {
        var client = factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/users/resolve",
            new { issuer = "https://idp.test/" + Guid.NewGuid(), subject = "person", email = (string?)null, displayName = "x" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ResolveBody>())!.TenantId;
    }

    [Fact]
    public async Task Another_tenant_cannot_offboard_anyone()
    {
        var victim = await NewUserTenantAsync();
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());

        var resp = await other.PostAsJsonAsync($"/api/data/tenants/{victim}/offboard", new { confirmTenantId = victim });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.GetAsync("/api/data/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_household_tenant_cannot_be_offboarded_and_the_confirmation_must_match()
    {
        var household = factory.CreateAuthenticatedClient(FlowLedgerApiFactory.DemoTenantId);

        var self = await household.PostAsJsonAsync(
            $"/api/data/tenants/{FlowLedgerApiFactory.DemoTenantId}/offboard", new { confirmTenantId = FlowLedgerApiFactory.DemoTenantId });
        self.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.NotFound);

        var victim = await NewUserTenantAsync();
        var mismatch = await household.PostAsJsonAsync($"/api/data/tenants/{victim}/offboard", new { confirmTenantId = Guid.NewGuid() });
        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unknown = Guid.NewGuid();
        var missing = await household.PostAsJsonAsync($"/api/data/tenants/{unknown}/offboard", new { confirmTenantId = unknown });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Household_admin_offboards_a_tenant_and_its_data_and_login_are_gone()
    {
        var victim = await NewUserTenantAsync();
        var victimClient = factory.CreateAuthenticatedClient(victim);
        (await victimClient.PostAsJsonAsync("/api/accounts", new FlowLedger.Contracts.CreateAccountRequest("Mine", "Checking", 10m, "USD", null, null)))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        var household = factory.CreateAuthenticatedClient(FlowLedgerApiFactory.DemoTenantId);

        var resp = await household.PostAsJsonAsync($"/api/data/tenants/{victim}/offboard", new { confirmTenantId = victim });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var identities = await victimClient.GetFromJsonAsync<List<object>>("/api/users/identities");
        identities.Should().BeEmpty();
        var accounts = await victimClient.GetFromJsonAsync<List<object>>("/api/accounts");
        accounts.Should().BeEmpty();

        var deleted = await household.GetFromJsonAsync<List<ListedTenant>>("/api/data/tenants?status=deleted");
        deleted!.Select(t => t.TenantId).Should().Contain(victim);
    }

    private sealed record ListedTenant(Guid TenantId, string Status);
}
