using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// The Transactions grid sorts on the server (#209): the order is applied in SQL before paging, so the second page of
/// "amount ascending" continues the first instead of re-sorting whatever 50 rows happened to be on screen.
/// </summary>
[Collection("Integration")]
public sealed class TransactionSortTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public TransactionSortTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<TestTenantContext> SeedAsync()
    {
        var tenant = TestTenantContext.New();
        var account = await _fixture.SeedAccountAsync(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        // amount order (9, 40, 15, 70, 25) deliberately differs from date order so a client-side sort of one page would be wrong.
        (string Name, decimal Amount, int Day)[] rows =
            [("Delta", 9m, 5), ("Alpha", 40m, 4), ("Echo", 15m, 3), ("Bravo", 70m, 2), ("Charlie", 25m, 1)];

        await using var db = _fixture.CreateDbContext(tenant);
        foreach (var (name, amount, day) in rows)
        {
            var date = new DateOnly(2026, 3, day);
            db.Transactions.Add(Transaction.RecordActual(
                tenantId, account, new Money(amount, Currency.Usd), TransactionDirection.Debit, name, date, date, TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider));
        }

        await db.SaveChangesAsync();
        return tenant;
    }

    [DockerFact]
    public async Task Amount_sort_orders_the_whole_result_across_pages()
    {
        var tenant = await SeedAsync();
        await using var db = _fixture.CreateDbContext(tenant);
        var repo = new TransactionRepository(db);

        var first = await repo.ListAsync(skip: 0, take: 2, sort: TransactionSortKey.Amount, descending: false);
        var second = await repo.ListAsync(skip: 2, take: 2, sort: TransactionSortKey.Amount, descending: false);

        first.Select(t => t.Amount.Amount).Should().Equal(9m, 15m);
        second.Select(t => t.Amount.Amount).Should().Equal(25m, 40m);
    }

    [DockerFact]
    public async Task Descending_and_text_sorts_are_applied_in_the_query()
    {
        var tenant = await SeedAsync();
        await using var db = _fixture.CreateDbContext(tenant);
        var repo = new TransactionRepository(db);

        (await repo.ListAsync(sort: TransactionSortKey.Amount, descending: true)).Select(t => t.Description)
            .Should().Equal("Bravo", "Alpha", "Charlie", "Echo", "Delta");
        (await repo.ListAsync(sort: TransactionSortKey.Description, descending: false)).Select(t => t.Description)
            .Should().Equal("Alpha", "Bravo", "Charlie", "Delta", "Echo");
    }

    [DockerFact]
    public async Task Default_order_is_newest_first_and_ascending_date_reverses_it()
    {
        var tenant = await SeedAsync();
        await using var db = _fixture.CreateDbContext(tenant);
        var repo = new TransactionRepository(db);

        (await repo.ListAsync()).Select(t => t.Description).Should().Equal("Delta", "Alpha", "Echo", "Bravo", "Charlie");
        (await repo.ListAsync(sort: TransactionSortKey.Date, descending: false)).Select(t => t.Description)
            .Should().Equal("Charlie", "Bravo", "Echo", "Alpha", "Delta");
    }
}
