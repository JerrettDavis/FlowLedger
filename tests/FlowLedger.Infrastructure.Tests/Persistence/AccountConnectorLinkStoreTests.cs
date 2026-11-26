using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#154: account_connector_links moved from an ad-hoc Npgsql table to EF (migration + tenant filter).</summary>
[Collection("Integration")]
public sealed class AccountConnectorLinkStoreTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public AccountConnectorLinkStoreTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private EfAccountConnectorLinkStore Store(TestTenantContext tenant) =>
        new(_fixture.CreateProductionStyleContext(tenant), tenant);

    [DockerFact]
    public async Task Links_are_one_per_account_and_one_account_per_provider_account()
    {
        var store = Store(TestTenantContext.New());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        (await store.LinkAsync(a, "SimpleFIN", "ACT-1")).Should().BeTrue();
        (await store.LinkAsync(b, "SimpleFIN", "ACT-1")).Should().BeFalse("a provider account feeds only one account");
        (await store.LinkAsync(b, "MX", "ACT-1")).Should().BeTrue("the same id under a different connector is a different provider account");

        // Re-link account a to another connector: replaces its previous link.
        (await store.LinkAsync(a, "MX", "ACT-9")).Should().BeTrue();
        (await store.FindByAccountAsync(a))!.ConnectorId.Should().Be("MX");
        (await store.FindByProviderAccountAsync("SimpleFIN", "ACT-1")).Should().BeNull();
        (await store.ListAsync()).Should().HaveCount(2);
    }

    [DockerFact]
    public async Task Pinned_manual_accounts_and_unlink_and_tenant_isolation()
    {
        var store = Store(TestTenantContext.New());
        var other = Store(TestTenantContext.New());
        var a = Guid.NewGuid();

        await store.PinManualAsync(a);
        (await store.FindByAccountAsync(a))!.ConnectorId.Should().Be(AccountConnectorLinks.Manual);
        (await other.FindByAccountAsync(a)).Should().BeNull("links are per tenant");
        (await other.ListAsync()).Should().BeEmpty();
        (await other.UnlinkAsync(a)).Should().BeFalse("another tenant's unlink must not touch this tenant's link");

        (await store.UnlinkAsync(a)).Should().BeTrue();
        (await store.UnlinkAsync(a)).Should().BeFalse();
        (await store.FindByAccountAsync(a)).Should().BeNull();
    }
}
