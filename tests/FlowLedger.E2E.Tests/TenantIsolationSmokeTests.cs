using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.E2E.Tests;

/// <summary>
/// Two-user isolation smoke test against the real stack (#138): data created by one tenant must be invisible to another tenant
/// over the API, and must not appear in the web UI, which here serves the household tenant. The exhaustive route sweep lives in
/// Api.Tests (TenantIsolationMatrixTests); this proves the same boundary holds through the composed containers.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class TenantIsolationSmokeTests : E2ETestBase
{
    private static HttpClient Client(Guid tenant)
    {
        var http = new HttpClient { BaseAddress = new Uri((Environment.GetEnvironmentVariable("E2E_API_URL") ?? "http://localhost:5001").TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Add("X-Api-Key", Environment.GetEnvironmentVariable("E2E_API_KEY") ?? string.Empty);
        http.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        return http;
    }

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

        // The web UI serves a different tenant (the household), so the account must not be rendered there either.
        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await AssertNoErrorAlertVisible();
        (await Page!.ContentAsync()).Should().NotContain(secretName);
        AssertNoPageErrors();
    }
}
