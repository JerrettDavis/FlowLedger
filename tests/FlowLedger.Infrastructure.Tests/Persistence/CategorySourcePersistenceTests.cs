using FluentAssertions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class CategorySourcePersistenceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public CategorySourcePersistenceTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Category_source_round_trips_and_defaults_to_manual()
    {
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);
        var account = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(10m, Currency.Usd));
        var category = Category.Create(tenantId, new CategoryPath("Food/Groceries"), "Groceries");
        var auto = Transaction.RecordActual(tenantId, account.AccountId, new Money(5m, Currency.Usd), TransactionDirection.Debit,
            "Bluejay", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), TransactionSource.Manual);
        var manual = Transaction.RecordActual(tenantId, account.AccountId, new Money(6m, Currency.Usd), TransactionDirection.Debit,
            "Other", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), TransactionSource.Manual);
        var plain = Transaction.RecordActual(tenantId, account.AccountId, new Money(7m, Currency.Usd), TransactionDirection.Debit,
            "Plain", new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3), TransactionSource.Manual);
        auto.Categorize(category.CategoryId, null, CategorySource.Auto);
        manual.Categorize(category.CategoryId);

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Accounts.Add(account);
            ctx.Categories.Add(category);
            ctx.Transactions.AddRange(auto, manual, plain);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var rows = await ctx.Transactions.AsNoTracking().ToListAsync();
            rows.Single(t => t.Id == auto.Id).CategorySource.Should().Be(CategorySource.Auto);
            rows.Single(t => t.Id == manual.Id).CategorySource.Should().Be(CategorySource.Manual);
            rows.Single(t => t.Id == plain.Id).CategorySource.Should().Be(CategorySource.Manual);
        }
    }
}
