using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace FlowLedger.E2E.Tests;

/// <summary>
/// Two-user isolation smoke test against the real stack (#138): data created by one tenant must be invisible to another tenant
/// over the API, and must not appear in the web UI, where the signed-in user (OIDC) is mapped to the household tenant. The exhaustive route sweep lives in
/// Api.Tests (TenantIsolationMatrixTests); this proves the same boundary holds through the composed containers.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class TenantIsolationSmokeTests : E2ETestBase
{
    private static HttpClient Client(Guid tenant) => E2EApi.CreateClient(tenant);

    [E2EFact(DisplayName = "Tenant isolation: one tenant's account is invisible to another tenant, over the API and in the UI")]
    public async Task One_tenants_account_is_invisible_to_another_tenant()
    {
        var secretName = $"ZZ-ISOLATION-{Guid.NewGuid():N}";
        using var tenantA = Client(Guid.NewGuid());
        using var tenantB = Client(Guid.NewGuid());

        var created = await tenantA.PostAsJsonAsync("api/accounts", new
        {
            name = secretName,
            accountType = "Checking",
            startingBalance = 10m,
            currency = "USD",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Control: the owner sees it.
        (await tenantA.GetStringAsync("api/accounts")).Should().Contain(secretName);

        // The other tenant sees neither the list entry nor the row by id.
        (await tenantB.GetStringAsync("api/accounts")).Should().NotContain(secretName);
        (await tenantB.GetAsync($"api/accounts/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Positive control: the signed-in browser is the household owner, so an account in the household tenant is rendered.
        var householdName = $"ZZ-HOUSEHOLD-{Guid.NewGuid():N}";
        using var household = Client(E2EApi.HouseholdTenantId);
        (await household.PostAsJsonAsync("api/accounts", new { name = householdName, accountType = "Checking", startingBalance = 10m, currency = "USD" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // The web UI serves the household tenant, so the other tenant's account must not be rendered there.
        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        await Assertions.Expect(Page!.GetByText(householdName).First).ToBeVisibleAsync();
        (await Page.ContentAsync()).Should().NotContain(secretName);
        AssertNoPageErrors();
    }
}
