using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class ConnectorEndpointTests(FlowLedgerApiFactory factory, ConnectorsOffApiFactory off) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Household_sees_all_connectors_with_the_simulated_one_active_and_no_secrets()
    {
        var resp = await factory.CreateAuthenticatedClient().GetAsync("/api/integrations/connectors");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("activeProvider").GetString().Should().Be("Simulated");
        doc.RootElement.GetProperty("selectionMode").GetString().Should().Be("configuration");
        var connectors = doc.RootElement.GetProperty("connectors").EnumerateArray().ToList();
        connectors.Select(c => c.GetProperty("id").GetString()).Should().Equal("Simulated", "SimpleFIN", "MX");
        connectors.Single(c => c.GetProperty("active").GetBoolean()).GetProperty("id").GetString().Should().Be("Simulated");
        connectors.Single(c => c.GetProperty("id").GetString() == "MX").GetProperty("usage").GetProperty("dailyBudget").GetInt32().Should().BeGreaterThan(0);
        raw.Should().NotContainEquivalentOf("accessurl\":\"").And.NotContain("@");
    }

    [Fact]
    public async Task Another_tenant_is_forbidden()
    {
        var resp = await factory.CreateAuthenticatedClient(Guid.NewGuid()).GetAsync("/api/integrations/connectors");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Feature_flag_off_hides_the_endpoint()
    {
        var resp = await off.CreateAuthenticatedClient().GetAsync("/api/integrations/connectors");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
