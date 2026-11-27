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
        var tenant = TestTenantContext.New();
        var store = Store(tenant);
        var a = (await _fixture.SeedAccountAsync(tenant)).Value;
        var b = (await _fixture.SeedAccountAsync(tenant)).Value;

        (await store.LinkAsync(a, "SimpleFIN", "ACT-1")).Should().BeTrue();
        (await store.LinkAsync(b, "SimpleFIN", "ACT-1")).Should().BeFalse("a provider account feeds only one account");
        (await store.LinkAsync(b, "MX", "ACT-1")).Should().BeTrue("the same id under a different connector is a different provider account");

        // Re-link account a to another connector: replaces its previous link.
        (await store.LinkAsync(a, "MX", "ACT-9")).Should().BeTrue();
        (await store.FindByAccountAsync(a))!.ConnectorId.Value.Should().Be("MX");
        (await store.FindByProviderAccountAsync("SimpleFIN", "ACT-1")).Should().BeNull();
        (await store.ListAsync()).Should().HaveCount(2);
    }

    [DockerFact]
    public async Task Pinned_manual_accounts_and_unlink_and_tenant_isolation()
    {
        var tenant = TestTenantContext.New();
        var store = Store(tenant);
        var other = Store(TestTenantContext.New());
        var a = (await _fixture.SeedAccountAsync(tenant)).Value;

        await store.PinManualAsync(a);
        (await store.FindByAccountAsync(a))!.ConnectorId.Value.Should().Be(AccountConnectorLinks.Manual);
        (await other.FindByAccountAsync(a)).Should().BeNull("links are per tenant");
        (await other.ListAsync()).Should().BeEmpty();
        (await other.UnlinkAsync(a)).Should().BeFalse("another tenant's unlink must not touch this tenant's link");

        (await store.UnlinkAsync(a)).Should().BeTrue();
        (await store.UnlinkAsync(a)).Should().BeFalse();
        (await store.FindByAccountAsync(a)).Should().BeNull();
    }

    [DockerFact]
    public async Task A_link_to_another_tenants_or_a_missing_account_is_rejected_by_the_database()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var foreignAccount = (await _fixture.SeedAccountAsync(b)).Value;

        var linkForeign = () => Store(a).LinkAsync(foreignAccount, "SimpleFIN", "ACT-1");
        await linkForeign.Should().ThrowAsync<Exception>("composite tenant FK (#146): the account belongs to tenant B");

        var linkMissing = () => Store(a).LinkAsync(Guid.NewGuid(), "SimpleFIN", "ACT-2");
        await linkMissing.Should().ThrowAsync<Exception>();
    }
}
