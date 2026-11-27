using System.Data.Common;
using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Application.Features.Query;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// The dashboard figures computed by SQL <c>GROUP BY</c> equal the ones the in-memory query engine computes over the same seeded data,
/// transfers, splits, hidden accounts and case variants included, and the SQL path never loads the history (#160).
/// </summary>
[Collection("Integration")]
public sealed class DashboardAggregationEquivalenceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public DashboardAggregationEquivalenceTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TestClock.Instant;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Sql)
            {
                Sql.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static DateOnly Ago(int days) => TestClock.Today.AddDays(-days);

    private static Money Usd(decimal amount) => new(amount, Currency.Usd);

    /// <summary>Seeds a household that exercises every rule the engine applies to a transaction.</summary>
    private async Task<TestTenantContext> SeedAsync(int randomRows)
    {
        var tenant = TestTenantContext.New();
        var tenantId = TenantId.From(tenant.TenantId);
        await using var db = _fixture.CreateDbContext(tenant);

        Account Make(string name, AccountType type, Action<Account>? tweak = null)
        {
            var a = Account.Create(tenantId, name, type, Usd(1000m), TestClock.Provider);
            tweak?.Invoke(a);
            db.Accounts.Add(a);
            return a;
        }

        var chk = Make("Checking", AccountType.Checking);
        var sav = Make("Savings", AccountType.Savings);
        var card = Make("Card", AccountType.CreditCard);
        var hidden = Make("Hidden", AccountType.Checking, a => a.ApplySettings(true, AccountMode.Standard, hideTransactions: true));
        var inactive = Make("Closed", AccountType.Checking, a => a.Deactivate());

        Category Cat(string path)
        {
            var c = Category.Create(tenantId, new CategoryPath(path), path.Split('/').Last());
            db.Categories.Add(c);
            return c;
        }

        var groceries = Cat("Food/Groceries");
        var restaurants = Cat("Food/Restaurants");
        var transport = Cat("Transport");
        var transfer = Cat("Transfer");
        var invest = Cat("Investments/Brokerage");
        var salary = Cat("Income/Salary");
        Category?[] spendCats = [groceries, restaurants, transport, null, null];

        Transaction Add(Account account, decimal amount, TransactionDirection dir, string description, int daysAgo, Category? category = null,
            string? merchant = null, int? postedDaysAgo = null, bool pending = false)
        {
            var effective = Ago(daysAgo);
            var posted = pending ? (DateOnly?)null : Ago(postedDaysAgo ?? daysAgo);
            var tx = Transaction.RecordActual(tenantId, account.AccountId, Usd(amount), dir, description, effective, posted, TransactionSource.Manual, TestClock.Provider);
            if (category is not null)
            {
                tx.Categorize(category.CategoryId, merchant, CategorySource.Manual);
            }
            else if (merchant is not null)
            {
                tx.SetMerchantName(merchant);
            }

            db.Transactions.Add(tx);
            return tx;
        }

        // Income and recurring bills.
        for (var m = 0; m < 7; m++)
        {
            Add(chk, 3000m, TransactionDirection.Credit, "ACME PAYROLL", m * 30 + 2, salary);
            Add(card, 59.99m, TransactionDirection.Debit, "NETFLIX", m * 30 + 5, restaurants, "Netflix");
        }

        // Case variants of one merchant (distinct dates), a blank and a whitespace-only merchant name, a posted date later than the effective date.
        Add(card, 4.50m, TransactionDirection.Debit, "STARBUCKS #1", 3, restaurants, "Starbucks");
        Add(card, 5.25m, TransactionDirection.Debit, "STARBUCKS #2", 9, restaurants, "STARBUCKS");
        Add(card, 6.75m, TransactionDirection.Debit, "STARBUCKS #3", 15, restaurants, "starbucks");
        Add(card, 12.00m, TransactionDirection.Debit, "NO MERCHANT SHOP", 4, transport);
        Add(card, 13.00m, TransactionDirection.Debit, "BLANK MERCHANT SHOP", 6, transport, "   ");
        Add(chk, 80m, TransactionDirection.Debit, "LATE POSTING", 40, groceries, "Grocer", postedDaysAgo: 36);
        Add(chk, 22m, TransactionDirection.Debit, "STILL PENDING", 1, groceries, "Grocer", pending: true);

        // Transfers: checking to savings (opposite directions), the same amount a few days apart, a chain of three legs of one amount,
        // a card payment (both legs debits, one side a liability, same description), a leg into a hidden account, and an unpaired leg.
        Add(chk, 500m, TransactionDirection.Debit, "TO SAVINGS", 20);
        Add(sav, 500m, TransactionDirection.Credit, "FROM CHECKING", 18);
        Add(chk, 700m, TransactionDirection.Debit, "XFER 700 A", 60);
        Add(sav, 700m, TransactionDirection.Credit, "XFER 700 B", 58);
        Add(sav, 700m, TransactionDirection.Credit, "XFER 700 C", 56);
        Add(chk, 700m, TransactionDirection.Debit, "XFER 700 D", 55);
        Add(chk, 250m, TransactionDirection.Debit, "VISA PAYMENT", 30);
        Add(card, 250m, TransactionDirection.Debit, "VISA PAYMENT", 29);
        Add(chk, 300m, TransactionDirection.Debit, "TO HIDDEN", 12);
        Add(hidden, 300m, TransactionDirection.Credit, "FROM CHECKING", 11);
        Add(chk, 123.45m, TransactionDirection.Debit, "UNPAIRED DEBIT", 8, groceries, "Grocer");
        Add(chk, 90m, TransactionDirection.Debit, "CATEGORIZED TRANSFER LEG", 14, transfer);
        Add(chk, 400m, TransactionDirection.Debit, "BROKERAGE", 17, invest);

        // Rows on accounts that are off the everyday views, and forecast rows that are not transactions at all.
        Add(hidden, 999m, TransactionDirection.Debit, "HIDDEN SPEND", 10, groceries, "Hiddenmart");
        Add(inactive, 888m, TransactionDirection.Debit, "CLOSED SPEND", 10, groceries, "Closedmart");
        db.Transactions.Add(Transaction.CreatePlanned(tenantId, chk.AccountId, Usd(777m), TransactionDirection.Debit, "PLANNED RENT", Ago(2), TestClock.Provider));
        var skipped = Transaction.CreatePlanned(tenantId, chk.AccountId, Usd(666m), TransactionDirection.Debit, "SKIPPED", Ago(3), TestClock.Provider);
        skipped.Skip();
        db.Transactions.Add(skipped);

        // Split transactions: each part counts in its own category; a part in a non-spending category does not count.
        var split1 = Add(card, 100m, TransactionDirection.Debit, "BIG BOX", 7, groceries, "Big Box");
        split1.SetSplits([new TransactionSplit(Usd(60m), groceries.CategoryId), new TransactionSplit(Usd(40m), transport.CategoryId)]);
        var split2 = Add(card, 50m, TransactionDirection.Debit, "BIG BOX 2", 2, null, "Big Box");
        split2.SetSplits([new TransactionSplit(Usd(30m), invest.CategoryId), new TransactionSplit(Usd(20m), null)]);

        // Volume: pseudo-random spend over the last seven months across several merchants.
        var rng = new Random(42);
        string[] merchants = ["Shell", "Trader Joe's", "Amazon", "Uber", "Chipotle", "Target", "SHELL", "amazon"];
        Account[] spendAccounts = [chk, card, chk, card, hidden];
        for (var i = 0; i < randomRows; i++)
        {
            var merchant = merchants[rng.Next(merchants.Length)];
            Add(spendAccounts[rng.Next(spendAccounts.Length)], Math.Round((decimal)(rng.NextDouble() * 120 + 1), 2), TransactionDirection.Debit,
                $"{merchant} #{i}", rng.Next(0, 210), spendCats[rng.Next(spendCats.Length)], merchant);
        }

        await db.SaveChangesAsync();

        // Net worth history: a snapshot a day for a few weeks on the active accounts (and one on the closed account).
        foreach (var a in new[] { chk, sav, card })
        {
            for (var d = 0; d < 20; d++)
            {
                var at = new DateTimeOffset(Ago(d).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
                db.BalanceSnapshots.Add(AccountBalanceSnapshotRecord.Create(tenant.TenantId, a.AccountId.Value, "Simulated", "p", 1000m + d * 7, "USD", at));
            }
        }

        db.BalanceSnapshots.Add(AccountBalanceSnapshotRecord.Create(
            tenant.TenantId, inactive.AccountId.Value, "Simulated", "p", 55m, "USD", new DateTimeOffset(Ago(1).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero)));
        await db.SaveChangesAsync();
        return tenant;
    }

    private static (GetDashboardInsightsHandler Engine, GetDashboardInsightsHandler Sql) Handlers(FlowLedgerDbContext db)
    {
        var clock = new FixedClock();
        var accounts = new AccountRepository(db);
        var categories = new CategoryRepository(db);
        var balance = new BalanceHistoryReader(db, clock);
        var datasets = new QueryDatasetProvider(new TransactionRepository(db), accounts, categories, new AssetRepository(db), balance, clock);
        var run = new RunQueryHandler(datasets, clock);
        var aggregates = new TransactionAggregates(new EfTransactionAggregateStore(db), accounts, categories);
        return (new GetDashboardInsightsHandler(run, clock), new GetDashboardInsightsHandler(run, clock, null, null, aggregates, balance, accounts));
    }

    public static IEnumerable<object?[]> Windows()
    {
        yield return [null, null];
        yield return [Ago(60), null];
        yield return [Ago(100), Ago(20)];
        yield return [Ago(30), Ago(30)];
        yield return [new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)];
        yield return [Ago(400), Ago(300)]; // empty
    }

    [DockerTheory]
    [MemberData(nameof(Windows))]
    public async Task The_sql_dashboard_equals_the_query_engine_dashboard_for_the_same_data(DateOnly? from, DateOnly? to)
    {
        var tenant = await SeedAsync(randomRows: 400);
        await using var db = _fixture.CreateDbContext(tenant);
        var (engine, sql) = Handlers(db);

        var expected = await engine.HandleAsync(from, to);
        var actual = await sql.HandleAsync(from, to);

        actual.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
        if (from is null && to is null)
        {
            // Guard against a vacuous comparison: the seed really produces every section.
            expected.SpendingByCategory.Should().NotBeEmpty();
            expected.CashFlow.Should().NotBeEmpty();
            expected.NetWorth.Should().NotBeEmpty();
            expected.TopMerchants.Should().NotBeEmpty();
        }
    }

    [DockerFact]
    public async Task Transfer_legs_hidden_accounts_planned_rows_and_non_spending_splits_are_left_out_exactly_as_the_engine_leaves_them_out()
    {
        var tenant = await SeedAsync(randomRows: 0);
        await using var db = _fixture.CreateDbContext(tenant);
        var accounts = new AccountRepository(db);
        var aggregates = new TransactionAggregates(new EfTransactionAggregateStore(db), accounts, new CategoryRepository(db));

        var merchants = await aggregates.SpendByMerchantAsync(Ago(400), Ago(0));
        var categories = await aggregates.SpendByCategoryAsync(Ago(400), Ago(0));

        merchants.Select(m => m.Merchant).Should().NotContain(["Hiddenmart", "Closedmart"], "hidden and disabled accounts are not counted");
        merchants.Should().NotContain(m => m.Merchant.Contains("XFER", StringComparison.Ordinal) || m.Merchant == "TO SAVINGS" || m.Merchant == "VISA PAYMENT",
            "paired transfer legs are not spending");
        merchants.Should().NotContain(m => m.Merchant == "PLANNED RENT" || m.Merchant == "SKIPPED");
        merchants.Single(m => m.Merchant.Equals("starbucks", StringComparison.OrdinalIgnoreCase)).Should().BeEquivalentTo(new MerchantTotal("Starbucks", 16.50m, 3));
        categories.Select(c => c.Category).Should().NotContain(["Transfer", "Investments/Brokerage"]);
        categories.Single(c => c.Category == "Food/Groceries").Total.Should().Be(80m + 22m + 123.45m + 60m, "a pending row counts, and a split transaction counts each part in its own category");
    }

    [DockerFact]
    public async Task The_sql_path_never_loads_the_history_and_issues_the_same_statements_whatever_the_volume()
    {
        var small = await QueryCountAsync(randomRows: 20);
        await _fixture.ResetAsync();
        var large = await QueryCountAsync(randomRows: 1500);

        small.Statements.Should().Be(large.Statements, "the number of queries does not grow with the number of transactions");
        large.Statements.Should().BeLessThan(40);
        large.TransactionReads.Should().OnlyContain(
            sql => sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) || sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase),
            "every read of the transactions table is a grouping, or the bounded transfer-candidate probe, never a history scan");
    }

    private async Task<(int Statements, List<string> TransactionReads)> QueryCountAsync(int randomRows)
    {
        var tenant = await SeedAsync(randomRows);
        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);
        var (_, sql) = Handlers(db);
        lock (log.Sql)
        {
            log.Sql.Clear();
        }

        await sql.HandleAsync(null, null);

        lock (log.Sql)
        {
            return (log.Sql.Count, log.Sql.Where(s => s.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).ToList());
        }
    }
}
