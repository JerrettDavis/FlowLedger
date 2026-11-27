using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#72: category, status and amount filters are applied in SQL, before paging and counting.</summary>
[Collection("Integration")]
public sealed class TransactionFilterTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private CategoryId _groceries;

    private async Task<TestTenantContext> SeedAsync()
    {
        var tenant = TestTenantContext.New();
        var account = await fixture.SeedAccountAsync(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        await using var db = fixture.CreateDbContext(tenant);
        var category = Category.Create(tenantId, new CategoryPath("Food/Groceries"), "Groceries");
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        _groceries = category.CategoryId;

        Transaction Make(string name, decimal amount, int day, bool categorized, bool pending)
        {
            var date = new DateOnly(2026, 3, day);
            var tx = pending
                ? Transaction.RecordActual(tenantId, account, new Money(amount, Currency.Usd), TransactionDirection.Debit, name, date, null, TransactionSource.Manual)
                : Transaction.RecordActual(tenantId, account, new Money(amount, Currency.Usd), TransactionDirection.Debit, name, date, date, TransactionSource.Manual);
            if (categorized)
            {
                tx.Categorize(_groceries);
            }

            return tx;
        }

        db.Transactions.AddRange(
            Make("Market", 40m, 1, categorized: true, pending: false),
            Make("Cafe", 6m, 2, categorized: false, pending: false),
            Make("Gadget", 300m, 3, categorized: false, pending: false),
            Make("Fuel", 55m, 4, categorized: true, pending: true));
        await db.SaveChangesAsync();
        return tenant;
    }

    [DockerFact]
    public async Task Category_uncategorized_status_and_amount_filters_apply_to_both_the_page_and_the_count()
    {
        var tenant = await SeedAsync();
        await using var db = fixture.CreateDbContext(tenant);
        var repo = new TransactionRepository(db);

        var byCategory = new TransactionFilter(CategoryId: _groceries.Value);
        (await repo.ListAsync(filter: byCategory)).Select(t => t.Description).Should().BeEquivalentTo("Market", "Fuel");
        (await repo.CountMatchingAsync(filter: byCategory)).Should().Be(2);

        var uncategorized = new TransactionFilter(UncategorizedOnly: true);
        (await repo.ListAsync(filter: uncategorized)).Select(t => t.Description).Should().BeEquivalentTo("Cafe", "Gadget");

        var pending = new TransactionFilter(Status: TransactionStatus.Pending);
        (await repo.ListAsync(filter: pending)).Select(t => t.Description).Should().Equal("Fuel");

        var midRange = new TransactionFilter(MinAmount: 10m, MaxAmount: 100m);
        (await repo.ListAsync(filter: midRange)).Select(t => t.Description).Should().BeEquivalentTo("Market", "Fuel");
        (await repo.CountMatchingAsync(filter: midRange)).Should().Be(2);

        var combined = new TransactionFilter(UncategorizedOnly: true, MinAmount: 100m);
        (await repo.ListAsync(filter: combined)).Select(t => t.Description).Should().Equal("Gadget");
    }

    [DockerFact]
    public async Task No_filter_returns_everything()
    {
        var tenant = await SeedAsync();
        await using var db = fixture.CreateDbContext(tenant);

        (await new TransactionRepository(db).CountMatchingAsync(filter: new TransactionFilter())).Should().Be(4);
    }

    [DockerFact]
    public async Task Provider_category_and_transfer_link_round_trip()
    {
        var tenant = TestTenantContext.New();
        var account = await fixture.SeedAccountAsync(tenant);
        var date = new DateOnly(2026, 3, 1);
        var link = Guid.NewGuid();
        Guid id;
        await using (var db = fixture.CreateDbContext(tenant))
        {
            var tx = Transaction.RecordActual(TenantId.From(tenant.TenantId), account, new Money(5m, Currency.Usd), TransactionDirection.Debit, "Hinted", date, date, TransactionSource.Manual);
            tx.SetProviderCategory("  Food & Dining  ");
            tx.LinkTransfer(link);
            db.Transactions.Add(tx);
            await db.SaveChangesAsync();
            id = tx.Id;
        }

        await using var verify = fixture.CreateDbContext(tenant);
        var loaded = await new TransactionRepository(verify).GetByIdAsync(TransactionId.From(id));
        loaded!.ProviderCategory.Should().Be("Food & Dining");
        loaded.TransferLinkId.Should().Be(link);
    }
}
