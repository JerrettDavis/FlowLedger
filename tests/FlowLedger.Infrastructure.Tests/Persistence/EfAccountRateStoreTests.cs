using FlowLedger.Application.Features.Strategies;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#87 slice 3: account_rates moved from an ad-hoc Npgsql table to EF (migration + tenant filter + tenant guard).</summary>
[Collection("Integration")]
public sealed class EfAccountRateStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Rates_are_tenant_scoped_upserted_and_removable()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var acct = (await fixture.SeedAccountAsync(a)).Value;

        await using (var db = fixture.CreateProductionStyleContext(a))
        {
            var store = new EfAccountRateStore(db, a);
            await store.UpsertAsync(a.TenantId, new AccountRate(acct, 4.25m, null));
            await store.UpsertAsync(a.TenantId, new AccountRate(acct, 5.5m, 120));
            var rates = await store.ListAsync(a.TenantId);
            rates.Should().ContainSingle().Which.Should().Be(new AccountRate(acct, 5.5m, 120));
        }

        await using (var db = fixture.CreateProductionStyleContext(b))
        {
            var store = new EfAccountRateStore(db, b);
            (await store.ListAsync(b.TenantId)).Should().BeEmpty("tenant B must not see tenant A's rates");
            await store.RemoveAsync(b.TenantId, acct);
        }

        await using (var db = fixture.CreateProductionStyleContext(a))
        {
            var store = new EfAccountRateStore(db, a);
            (await store.ListAsync(a.TenantId)).Should().HaveCount(1, "tenant B's delete must not touch tenant A");
            await store.RemoveAsync(a.TenantId, acct);
            (await store.ListAsync(a.TenantId)).Should().BeEmpty();
        }
    }

    [DockerFact]
    public async Task Asking_for_another_tenants_rates_fails_loudly()
    {
        var a = TestTenantContext.New();
        await using var db = fixture.CreateProductionStyleContext(a);
        var store = new EfAccountRateStore(db, a);

        var act = () => store.ListAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [DockerFact]
    public async Task Out_of_range_rates_are_still_rejected_by_the_database_check()
    {
        var a = TestTenantContext.New();
        await using var db = fixture.CreateProductionStyleContext(a);
        var store = new EfAccountRateStore(db, a);

        var act = () => store.UpsertAsync(a.TenantId, new AccountRate(Guid.NewGuid(), 150m, null));

        await act.Should().ThrowAsync<Exception>();
    }
}
