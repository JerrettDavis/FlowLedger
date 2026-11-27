using FlowLedger.Application.Features.Categories;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#68/#67: the transfer link and the provider-category hint survive the real EF store, including the partner leg of a scoped run.</summary>
[Collection("Integration")]
public sealed class TransferLinkPersistenceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Transaction Leg(TenantId tenant, AccountId account, decimal amount, TransactionDirection dir, string text, DateOnly date)
        => Transaction.RecordActual(tenant, account, new Money(amount, Currency.Usd), dir, text, date, date, TransactionSource.MxAggregation, FlowLedger.TestSupport.TestClock.Provider);

    [DockerFact]
    public async Task Categorization_persists_one_link_id_on_both_transfer_legs_through_the_real_store()
    {
        var tenant = TestTenantContext.New();
        var tenantId = TenantId.From(tenant.TenantId);
        var checking = await fixture.SeedAccountAsync(tenant, "Checking");
        var savings = await fixture.SeedAccountAsync(tenant, "Savings");
        var date = new DateOnly(2026, 11, 3);
        Guid outId, inId;
        await using (var seed = fixture.CreateDbContext(tenant))
        {
            var outLeg = Leg(tenantId, checking, 500m, TransactionDirection.Debit, "To savings", date);
            var inLeg = Leg(tenantId, savings, 500m, TransactionDirection.Credit, "From checking", date);
            seed.Transactions.AddRange(outLeg, inLeg);
            await seed.SaveChangesAsync();
            (outId, inId) = (outLeg.Id, inLeg.Id);
        }

        await using (var db = fixture.CreateDbContext(tenant))
        {
            var service = new CategorizationService(new CategorizationStore(db), new CategoryRepository(db), new AccountRepository(db), tenant, new FlowLedger.Application.Features.Categories.CategorizationOptions(), new FlowLedger.Application.Features.LocalAi.LocalAiOptions());
            var result = await service.RunAsync();
            result.TransfersLinked.Should().Be(2);
        }

        await using var verify = fixture.CreateDbContext(tenant);
        var rows = await verify.Transactions.AsNoTracking().Where(t => t.Id == outId || t.Id == inId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(t => t.TransferLinkId).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
    }

    [DockerFact]
    public async Task A_scoped_run_for_one_new_leg_also_links_the_older_partner_leg()
    {
        var tenant = TestTenantContext.New();
        var tenantId = TenantId.From(tenant.TenantId);
        var checking = await fixture.SeedAccountAsync(tenant, "Checking");
        var savings = await fixture.SeedAccountAsync(tenant, "Savings");
        var date = new DateOnly(2026, 11, 3);
        Guid olderId, newId;
        await using (var seed = fixture.CreateDbContext(tenant))
        {
            var older = Leg(tenantId, checking, 250m, TransactionDirection.Debit, "To savings", date);
            var newer = Leg(tenantId, savings, 250m, TransactionDirection.Credit, "From checking", date.AddDays(1));
            seed.Transactions.AddRange(older, newer);
            await seed.SaveChangesAsync();
            (olderId, newId) = (older.Id, newer.Id);
        }

        await using (var db = fixture.CreateDbContext(tenant))
        {
            var service = new CategorizationService(new CategorizationStore(db), new CategoryRepository(db), new AccountRepository(db), tenant, new FlowLedger.Application.Features.Categories.CategorizationOptions(), new FlowLedger.Application.Features.LocalAi.LocalAiOptions());
            await service.RunForAsync([newId]);
        }

        await using var verify = fixture.CreateDbContext(tenant);
        var links = await verify.Transactions.AsNoTracking().Where(t => t.Id == olderId || t.Id == newId).Select(t => t.TransferLinkId).ToListAsync();
        links.Should().OnlyContain(l => l != null);
        links.Distinct().Should().HaveCount(1, "both legs share the link even though only one was in the sync's scope");
    }
}
