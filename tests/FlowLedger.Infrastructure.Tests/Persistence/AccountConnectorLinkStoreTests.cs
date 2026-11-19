using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class AccountConnectorLinkStoreTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly List<Guid> _tenants = [];

    public AccountConnectorLinkStoreTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    // Ad-hoc tables are not in Respawn's reset set, so remove what these tests wrote.
    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM account_connector_links WHERE tenant_id = ANY(@t)", conn);
        cmd.Parameters.AddWithValue("t", _tenants.ToArray());
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // table never created
        }
    }

    private sealed class Tenant(Guid id) : ITenantContext
    {
        public Guid TenantId { get; } = id;
        public Guid UserId { get; } = Guid.NewGuid();
    }

    private NpgsqlAccountConnectorLinkStore Store(out Guid tenant)
    {
        tenant = Guid.NewGuid();
        _tenants.Add(tenant);
        return new NpgsqlAccountConnectorLinkStore(_fixture.ConnectionString, new Tenant(tenant));
    }

    [DockerFact]
    public async Task Links_are_one_per_account_and_one_account_per_provider_account()
    {
        var store = Store(out _);
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
        var store = Store(out _);
        var other = Store(out _);
        var a = Guid.NewGuid();

        await store.PinManualAsync(a);
        (await store.FindByAccountAsync(a))!.ConnectorId.Should().Be(AccountConnectorLinks.Manual);
        (await other.FindByAccountAsync(a)).Should().BeNull("links are per tenant");

        (await store.UnlinkAsync(a)).Should().BeTrue();
        (await store.UnlinkAsync(a)).Should().BeFalse();
        (await store.FindByAccountAsync(a)).Should().BeNull();
    }
}
