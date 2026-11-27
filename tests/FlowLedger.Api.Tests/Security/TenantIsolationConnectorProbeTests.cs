using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Issue #136: the three tenant-scoped routes that need an enabled connector, a Quiltt API and a model. They run on
/// <see cref="ConnectorProbeApiFactory"/> (Quiltt enabled against a stub, fake query translator), so each can be probed for real:
/// tenant B must see none of tenant A's provider catalogue, Quiltt profile or names, and A's state must not change.
/// </summary>
[Collection("ApiIntegration")]
public sealed class TenantIsolationConnectorProbeTests(ConnectorProbeApiFactory factory) : IAsyncLifetime
{
    private const string Secret = TenantIsolationMatrixTests.Secret;
    private static readonly Guid TenantA = TenantIsolationMatrixTests.TenantA;
    private static readonly Guid TenantB = TenantIsolationMatrixTests.TenantB;

    public Task InitializeAsync()
    {
        factory.QuilttSessionRequests.Clear();
        factory.TranslatorPrompts.Clear();
        return factory.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedCatalogueAsync(Guid tenant, string providerAccountId, string name)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO connector_provider_accounts (tenant_id, connector_id, provider_account_id, name, account_type, balance, currency, last_seen_utc)
              VALUES (@t, 'Quiltt', @p, @n, 'Checking', 12.34, 'USD', now())", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("p", providerAccountId);
        cmd.Parameters.AddWithValue("n", name);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Available_accounts_lists_only_the_callers_own_provider_catalogue()
    {
        await SeedCatalogueAsync(TenantA, "prov-a-1", $"{Secret} Provider Checking");
        var a = factory.CreateAuthenticatedClient(TenantA);
        var b = factory.CreateAuthenticatedClient(TenantB);
        const string url = "/api/integrations/connectors/Quiltt/available-accounts";

        var asA = await a.GetStringAsync(url);
        asA.Should().Contain(Secret, "the control: tenant A sees its own catalogue, so an empty answer for B means something");

        var asB = await b.GetAsync(url);
        asB.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyB = await asB.Content.ReadAsStringAsync();
        bodyB.Should().NotContain(Secret).And.NotContain("prov-a-1");
        JsonDocument.Parse(bodyB).RootElement.GetArrayLength().Should().Be(0);

        (await a.GetStringAsync(url)).Should().Be(asA, "B's call must not change A's catalogue");
    }

    [Fact]
    public async Task Quiltt_session_never_reuses_or_exposes_another_tenants_profile()
    {
        var a = factory.CreateAuthenticatedClient(TenantA);
        var b = factory.CreateAuthenticatedClient(TenantB);

        var first = await a.PostAsync("/api/integrations/quiltt/session", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var profileA = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profileId").GetString()!;
        var connectionsA = await a.GetStringAsync("/api/integrations/connections");
        connectionsA.Should().Contain("Quiltt", "A's new profile is stored against A");

        var asB = await b.PostAsync("/api/integrations/quiltt/session", null);
        asB.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyB = await asB.Content.ReadAsStringAsync();
        var profileB = JsonDocument.Parse(bodyB).RootElement.GetProperty("profileId").GetString()!;

        profileB.Should().NotBe(profileA, "B must get its own Quiltt profile");
        bodyB.Should().NotContain(profileA);
        factory.QuilttSessionRequests.Should().HaveCount(2);
        factory.QuilttSessionRequests[1].Should().NotContain(profileA, "B's session request must not name A's profile: B has none yet, so Quiltt creates one");
        (await a.GetStringAsync("/api/integrations/connections")).Should().Be(connectionsA, "B's call must not change A's connection");

        // Control: A's next call reuses A's profile (the store lookup is per tenant, so this is what B would have got if it leaked).
        var again = await (await a.PostAsync("/api/integrations/quiltt/session", null)).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("profileId").GetString().Should().Be(profileA);
    }

    [Fact]
    public async Task Ask_builds_its_prompt_from_the_callers_own_data_only()
    {
        var a = factory.CreateAuthenticatedClient(TenantA);
        var b = factory.CreateAuthenticatedClient(TenantB);
        await TenantIsolationMatrixTests.SeedTenantAAsync(a);

        var asA = await a.PostAsJsonAsync("/api/query/ask", new { question = "what did I spend?" });
        asA.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.TranslatorPrompts.Should().ContainSingle().Which.Should().Contain(Secret, "the control: A's own names reach A's prompt");

        factory.TranslatorPrompts.Clear();
        var asB = await b.PostAsJsonAsync("/api/query/ask", new { question = "what did I spend?" });
        asB.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await asB.Content.ReadAsStringAsync();
        body.Should().NotContain(Secret);
        factory.TranslatorPrompts.Should().ContainSingle().Which.Should().NotContain(Secret, "B's model prompt must carry none of A's category or account names");
    }
}
