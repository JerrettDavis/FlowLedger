using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// #157 slices 2 and 3: transactions, recurring flows, categories, budgets and goals carry the xmin concurrency token, so a write
/// made from a stale copy fails instead of silently overwriting; background pipelines re-run on fresh data instead of failing.
/// </summary>
[Collection("Integration")]
public sealed class RowVersionConcurrencyTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public RowVersionConcurrencyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateOnly Day = new(2026, 3, 1);

    private async Task<(Guid TxId, Guid CategoryId)> SeedAsync(TestTenantContext tenant)
    {
        var accountId = await _fixture.SeedAccountAsync(tenant, "Checking");
        await using var db = _fixture.CreateDbContext(tenant);
        var tx = Transaction.RecordActual(TenantId.From(tenant.TenantId), accountId, new Money(12m, Currency.Usd),
            TransactionDirection.Debit, "Shop", Day, Day, TransactionSource.Manual);
        var category = Category.Create(TenantId.From(tenant.TenantId), new CategoryPath("Food"), "Food");
        db.Transactions.Add(tx);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return (tx.Id, category.Id);
    }

    [DockerFact]
    public async Task A_stale_transaction_write_is_rejected_not_silently_applied()
    {
        var tenant = TestTenantContext.New();
        var (txId, _) = await SeedAsync(tenant);
        await using var stale = _fixture.CreateDbContext(tenant);
        await using var other = _fixture.CreateDbContext(tenant);
        var mine = await stale.Transactions.SingleAsync(t => t.Id == txId);
        var theirs = await other.Transactions.SingleAsync(t => t.Id == txId);
        theirs.SetNote("edited elsewhere");
        await other.SaveChangesAsync();

        mine.SetNote("my edit");
        var act = () => stale.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var check = _fixture.CreateDbContext(tenant);
        (await check.Transactions.SingleAsync(t => t.Id == txId)).Notes.Should().Be("edited elsewhere");
    }

    [DockerFact]
    public async Task A_stale_category_rename_and_a_stale_recurring_flow_write_are_rejected()
    {
        var tenant = TestTenantContext.New();
        var (_, categoryId) = await SeedAsync(tenant);
        var accountId = await _fixture.SeedAccountAsync(tenant, "Bills");
        Guid flowId;
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            var flow = RecurringFlow.Create(TenantId.From(tenant.TenantId), accountId, "Rent", new Money(900m, Currency.Usd),
                TransactionDirection.Debit, RecurrencePattern.Monthly(1), Day);
            seed.RecurringFlows.Add(flow);
            await seed.SaveChangesAsync();
            flowId = flow.Id;
        }

        await using var a = _fixture.CreateDbContext(tenant);
        await using var b = _fixture.CreateDbContext(tenant);
        var cat1 = await a.Categories.SingleAsync(c => c.Id == categoryId);
        var cat2 = await b.Categories.SingleAsync(c => c.Id == categoryId);
        var flow1 = await a.RecurringFlows.SingleAsync(f => f.Id == flowId);
        var flow2 = await b.RecurringFlows.SingleAsync(f => f.Id == flowId);

        cat2.Rename(new CategoryPath("Dining"), "Dining");
        flow2.Deactivate();
        await b.SaveChangesAsync();

        cat1.Rename(new CategoryPath("Eating"), "Eating");
        await ((Func<Task>)(() => a.SaveChangesAsync())).Should().ThrowAsync<DbUpdateConcurrencyException>();
        a.ChangeTracker.Clear();
        (await a.RecurringFlows.SingleAsync(f => f.Id == flowId)).IsActive.Should().BeFalse();
        flow1 = await a.RecurringFlows.SingleAsync(f => f.Id == flowId);
        flow1.UpdateAmount(new Money(950m, Currency.Usd), flow1.AmountModel);
        // the reloaded copy is current, so its write succeeds
        await a.SaveChangesAsync();
    }

    [DockerFact]
    public async Task Budgets_and_goals_map_xmin_as_their_concurrency_token()
    {
        await using var db = _fixture.CreateDbContext(TestTenantContext.New());
        foreach (var entity in new[] { typeof(FlowLedger.Infrastructure.Persistence.Entities.BudgetRecord), typeof(FlowLedger.Infrastructure.Persistence.Entities.GoalRecord) })
        {
            var prop = db.Model.FindEntityType(entity)!.FindProperty("RowVersion")!;
            prop.IsConcurrencyToken.Should().BeTrue();
            prop.GetColumnName().Should().Be("xmin");
        }
    }

    [DockerFact]
    public async Task ConcurrencyRetry_reruns_a_stale_pipeline_on_fresh_rows_and_keeps_the_users_edit()
    {
        var tenant = TestTenantContext.New();
        var (txId, categoryId) = await SeedAsync(tenant);
        await using var pipeline = _fixture.CreateDbContext(tenant);
        var attempts = 0;

        // An auto-categorizer: only fills a missing category. The user sets one by hand between its read and its write.
        await ConcurrencyRetry.RunAsync(pipeline, async () =>
        {
            attempts++;
            var tx = await pipeline.Transactions.SingleAsync(t => t.Id == txId);
            if (attempts == 1)
            {
                await using var user = _fixture.CreateDbContext(tenant);
                var theirs = await user.Transactions.SingleAsync(t => t.Id == txId);
                theirs.Categorize(CategoryId.From(categoryId));
                await user.SaveChangesAsync();
            }

            if (tx.CategoryId is null)
            {
                tx.Categorize(CategoryId.From(Guid.NewGuid()), null, CategorySource.Auto);
            }

            await pipeline.SaveChangesAsync();
        }, NullLogger.Instance, "test pipeline", CancellationToken.None);

        attempts.Should().Be(2, "the stale write was retried, not lost and not thrown");
        await using var check = _fixture.CreateDbContext(tenant);
        var row = await check.Transactions.SingleAsync(t => t.Id == txId);
        row.CategoryId!.Value.Should().Be(CategoryId.From(categoryId));
        row.CategorySource.Should().Be(CategorySource.Manual, "the user's edit wins; the retry saw it and did nothing");
    }

    [DockerFact]
    public async Task ConcurrencyRetry_surfaces_the_conflict_after_the_attempts_are_used_up()
    {
        var tenant = TestTenantContext.New();
        var (txId, _) = await SeedAsync(tenant);
        await using var pipeline = _fixture.CreateDbContext(tenant);

        var act = () => ConcurrencyRetry.RunAsync(pipeline, async () =>
        {
            var tx = await pipeline.Transactions.SingleAsync(t => t.Id == txId);
            await using var user = _fixture.CreateDbContext(tenant);
            (await user.Transactions.SingleAsync(t => t.Id == txId)).SetNote(Guid.NewGuid().ToString());
            await user.SaveChangesAsync(); // always wins the race
            tx.SetNote("pipeline");
            await pipeline.SaveChangesAsync();
        }, NullLogger.Instance, "always losing", CancellationToken.None, maxAttempts: 2);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
