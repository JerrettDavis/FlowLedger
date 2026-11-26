using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#154: connector_provider_accounts moved from an ad-hoc Npgsql table to EF (migration + tenant filter).</summary>
[Collection("Integration")]
public sealed class EfProviderAccountCatalogTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private EfProviderAccountCatalog Catalog(TestTenantContext tenant) => new(fixture.CreateProductionStyleContext(tenant), tenant);

    [DockerFact]
    public async Task Catalog_is_upserted_filtered_by_connector_and_tenant_scoped()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var seen = DateTimeOffset.UtcNow;
        var catalog = Catalog(a);

        await catalog.UpsertAsync([
            new DiscoveredProviderAccount("SimpleFIN", "p1", "Checking", "checking", 10.5m, "USD", seen),
            new DiscoveredProviderAccount("MX", "p1", "Savings", "savings", 20m, "USD", seen),
        ]);
        await catalog.UpsertAsync([new DiscoveredProviderAccount("SimpleFIN", "p1", "Checking renamed", "checking", 99.1234m, "USD", seen)]);

        (await catalog.ListAsync()).Should().HaveCount(2);
        var sf = await catalog.ListAsync("SimpleFIN");
        sf.Should().ContainSingle().Which.Should().Match<DiscoveredProviderAccount>(x => x.Name == "Checking renamed" && x.Balance == 99.1234m);

        (await Catalog(b).ListAsync()).Should().BeEmpty("tenant B must not see tenant A's catalog");
    }
}
