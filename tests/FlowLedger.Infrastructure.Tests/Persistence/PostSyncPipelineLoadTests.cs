using System.Data.Common;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Contracts;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// The post-sync steps (aliases, rules, categorization, enrichment) read the history without tracking it and write back only the
/// rows they changed, in bounded batches (#163): one history SELECT and a tracker that never holds the whole history.
/// </summary>
[Collection("Integration")]
public sealed class PostSyncPipelineLoadTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public PostSyncPipelineLoadTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<(TestTenantContext Tenant, int Count)> SeedAsync(int count)
    {
        var tenant = TestTenantContext.New();
        var account = await _fixture.SeedAccountAsync(tenant);
        await using var seed = _fixture.CreateDbContext(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        for (var i = 0; i < count; i++)
        {
            seed.Transactions.Add(Transaction.RecordActual(
                tenantId, account, new Money(5m + i, Currency.Usd), TransactionDirection.Debit,
                $"COFFEE SHOP {i}", new DateOnly(2026, 1, 1).AddDays(i % 300), new DateOnly(2026, 1, 1).AddDays(i % 300), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider));
        }

        await seed.SaveChangesAsync();
        return (tenant, count);
    }

    [DockerFact]
    public async Task Alias_apply_scoped_to_ids_reads_only_those_rows_and_leaves_the_rest_untouched()
    {
        var (tenant, count) = await SeedAsync(1200);
        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);
        var aliases = new EfMerchantAliasStore(db, tenant);
        await aliases.SetAsync("COFFEE SHOP", "Cafe");
        var service = new MerchantAliasService(aliases, new CategorizationStore(db));
        List<Guid> scope;
        await using (var pick = _fixture.CreateDbContext(tenant))
        {
            scope = await pick.Transactions.OrderBy(t => t.Description).Take(10).Select(t => t.Id).ToListAsync();
        }

        log.Sql.Clear();
        var changed = await service.ApplyToAsync(scope);

        changed.Should().Be(10);
        var historySelects = log.Sql.Where(sql => sql.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).ToList();
        historySelects.Should().ContainSingle("one id lookup, no history scan");
        historySelects[0].Should().NotContain("LIMIT", "the lookup is by id, not a newest-first window");
        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Transactions.CountAsync(t => t.MerchantName == "Cafe")).Should().Be(10, "rows outside the sync's scope are not touched");
        (await verify.Transactions.CountAsync()).Should().Be(count);
    }

    [DockerFact]
    public async Task Alias_apply_loads_history_once_untracked_and_writes_only_changed_rows()
    {
        var (tenant, count) = await SeedAsync(1200);
        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);
        var aliases = new EfMerchantAliasStore(db, tenant);
        await aliases.SetAsync("COFFEE SHOP", "Cafe");
        var service = new MerchantAliasService(aliases, new CategorizationStore(db));
        log.Sql.Clear();

        var changed = await service.ApplyAsync();

        changed.Should().Be(count);
        log.Sql.Count(sql => sql.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).Should().Be(1, "one history read, no per-row lookups");
        db.ChangeTracker.Entries<Transaction>().Should().BeEmpty("saved batches are detached, the history is never tracked");
        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Transactions.CountAsync(t => t.MerchantName == "Cafe")).Should().Be(count);
    }

    [DockerFact]
    public async Task Alias_apply_with_nothing_to_change_writes_nothing()
    {
        var (tenant, _) = await SeedAsync(50);
        await using var db = _fixture.CreateDbContext(tenant);
        var aliases = new EfMerchantAliasStore(db, tenant);
        await aliases.SetAsync("NO SUCH TEXT", "Nothing");

        var changed = await new MerchantAliasService(aliases, new CategorizationStore(db)).ApplyAsync();

        changed.Should().Be(0);
        db.ChangeTracker.Entries<Transaction>().Should().BeEmpty();
    }

    private async Task<(TestTenantContext Tenant, CommandLog Log, FlowLedgerDbContext Db)> OpenLoggedAsync(TestTenantContext tenant)
    {
        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        return (tenant, log, new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant));
    }

    [DockerFact]
    public async Task Categorization_with_nothing_uncategorized_reads_no_history()
    {
        // A sync that brought no new rows must not re-read the whole history just to find nothing to do (#163).
        var tenant = TestTenantContext.New();
        var account = await _fixture.SeedAccountAsync(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            var category = Category.Create(tenantId, new CategoryPath("Food/Coffee"), "Coffee");
            seed.Categories.Add(category);
            for (var i = 0; i < 300; i++)
            {
                var tx = Transaction.RecordActual(
                    tenantId, account, new Money(5m + i, Currency.Usd), TransactionDirection.Debit,
                    $"COFFEE SHOP {i}", new DateOnly(2026, 1, 1).AddDays(i % 200), new DateOnly(2026, 1, 1).AddDays(i % 200), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);
                tx.Categorize(category.CategoryId, null, CategorySource.Manual);
                seed.Transactions.Add(tx);
            }

            await seed.SaveChangesAsync();
        }

        var (_, log, db) = await OpenLoggedAsync(tenant);
        await using (db)
        {
            var service = new FlowLedger.Application.Features.Categories.CategorizationService(
                new CategorizationStore(db), new CategoryRepository(db), new AccountRepository(db), tenant);
            log.Sql.Clear();

            var result = await service.RunAsync();

            result.Categorized.Should().Be(0);
            var transactionReads = log.Sql.Where(sql => sql.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).ToList();
            transactionReads.Should().ContainSingle("only the existence probe runs");
            transactionReads[0].Should().Contain("EXISTS", "an uncategorized-row probe, not a newest-first history window");
            db.ChangeTracker.Entries<Transaction>().Should().BeEmpty();
        }
    }

    private sealed class FixedSync(SyncResult result) : FlowLedger.Application.Abstractions.IFinancialSyncService
    {
        public Task<string> ConnectAsync(CancellationToken ct = default) => Task.FromResult("m");
        public Task<SyncResult> SyncAsync(CancellationToken ct = default) => Task.FromResult(result);
    }

    private static FlowLedger.Infrastructure.Sync.CategorizingSyncService BuildPipeline(
        FlowLedgerDbContext db, TestTenantContext tenant, SyncResult result)
    {
        var store = new CategorizationStore(db);
        return new FlowLedger.Infrastructure.Sync.CategorizingSyncService(
            new FixedSync(result), db,
            new FlowLedger.Application.Features.Categories.CategorizationService(store, new CategoryRepository(db), new AccountRepository(db), tenant),
            new MerchantAliasService(new EfMerchantAliasStore(db, tenant), store),
            new FlowLedger.Application.Features.Categories.CategoryRuleService(new EfCategoryRuleStore(db, tenant), store, new CategoryRepository(db), tenant),
            new TransactionEnrichmentService(store),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowLedger.Infrastructure.Sync.CategorizingSyncService>.Instance);
    }

    [DockerFact]
    public async Task A_sync_that_brought_no_new_rows_issues_no_transaction_select_even_with_lingering_uncategorized_rows()
    {
        var tenant = TestTenantContext.New();
        var account = await _fixture.SeedAccountAsync(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            for (var i = 0; i < 200; i++)
            {
                seed.Transactions.Add(Transaction.RecordActual(
                    tenantId, account, new Money(5m + i, Currency.Usd), TransactionDirection.Debit,
                    $"UNKNOWN VENDOR {i}", new DateOnly(2026, 1, 1).AddDays(i % 200), new DateOnly(2026, 1, 1).AddDays(i % 200), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider));
            }

            await seed.SaveChangesAsync();
        }

        var (_, log, db) = await OpenLoggedAsync(tenant);
        await using (db)
        {
            var pipeline = BuildPipeline(db, tenant, new SyncResult(0, 0, 0, 0, ChangedTransactionIds: []));
            log.Sql.Clear();

            await pipeline.SyncAsync();

            log.Sql.Where(sql => sql.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty(
                "nothing changed, so no step may read the table (the old pipeline re-read the history to retry lingering rows)");
        }
    }

    [DockerFact]
    public async Task A_sync_with_new_rows_reads_them_by_id_plus_a_bounded_window_and_a_bounded_learning_set_never_the_whole_table()
    {
        var tenant = TestTenantContext.New();
        var account = await _fixture.SeedAccountAsync(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        var coffee = Category.Create(tenantId, new CategoryPath("Food/Coffee"), "Coffee");
        var newIds = new List<Guid>();
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            seed.Categories.Add(coffee);
            for (var i = 0; i < 400; i++)
            {
                var day = new DateOnly(2026, 1, 1).AddDays(i % 200);
                var tx = Transaction.RecordActual(
                    tenantId, account, new Money(5m + i, Currency.Usd), TransactionDirection.Debit,
                    i % 2 == 0 ? "BLUE BOTTLE" : $"UNKNOWN VENDOR {i}", day, day, TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);
                if (i % 2 == 0)
                {
                    tx.Categorize(coffee.CategoryId, null, CategorySource.Manual);
                }

                seed.Transactions.Add(tx);
            }

            for (var i = 0; i < 3; i++)
            {
                var tx = Transaction.RecordActual(
                    tenantId, account, new Money(77m + i, Currency.Usd), TransactionDirection.Debit,
                    "BLUE BOTTLE", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);
                seed.Transactions.Add(tx);
                newIds.Add(tx.Id);
            }

            await seed.SaveChangesAsync();
        }

        var (_, log, db) = await OpenLoggedAsync(tenant);
        await using (db)
        {
            var pipeline = BuildPipeline(db, tenant, new SyncResult(0, 3, 0, 0, ChangedTransactionIds: newIds));
            log.Sql.Clear();

            await pipeline.SyncAsync();

            var reads = log.Sql.Where(sql => sql.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).ToList();
            reads.Should().NotBeEmpty();
            reads.Should().OnlyContain(
                sql => sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase)
                    && (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) || sql.Contains("ANY", StringComparison.OrdinalIgnoreCase) || sql.Contains(" IN ", StringComparison.OrdinalIgnoreCase)),
                "every read is an id lookup or a filtered, limited window; none is a full-table scan");
        }

        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Transactions.CountAsync(t => newIds.Contains(t.Id) && t.CategoryId == coffee.CategoryId)).Should().Be(3, "the new rows are categorized by the learned merchant");
        (await verify.Transactions.CountAsync(t => t.CategoryId == null && !newIds.Contains(t.Id))).Should().Be(200, "older uncategorized rows are not touched by a sync scoped to the new ones");
    }
}
