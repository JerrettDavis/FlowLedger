using System.Data.Common;
using FlowLedger.Application.Features.Insights;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// /api/insights/breakdown computed by SQL <c>GROUP BY</c> equals the in-memory analyzer over the same seeded data (transfers, splits, tags,
/// hidden accounts, case variants, non-spending categories included), and the SQL path never loads the history (#160).
/// </summary>
[Collection("Integration")]
public sealed class BreakdownAggregationEquivalenceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public BreakdownAggregationEquivalenceTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

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

    private sealed record Seeded(TestTenantContext Tenant, Guid Checking, Guid Card);

    private async Task<Seeded> SeedAsync(int randomRows)
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
        var closed = Make("Closed", AccountType.Checking, a => a.Deactivate());

        Category Cat(string path)
        {
            var c = Category.Create(tenantId, new CategoryPath(path), path.Split('/').Last());
            db.Categories.Add(c);
            return c;
        }

        var groceries = Cat("Food/Groceries");
        var restaurants = Cat("Food/Restaurants");
        var food = Cat("Food");
        var transport = Cat("Transport");
        var transfer = Cat("Transfer");
        var invest = Cat("Investments/Brokerage");
        var salary = Cat("Income/Salary");
        var bonus = Cat("Income/Bonus");
        Category?[] spendCats = [groceries, restaurants, food, transport, null, null];
        string[][] tagSets = [[], [], ["trip"], ["trip", "work"], ["Work"], ["fun"]];

        Transaction Add(Account account, decimal amount, TransactionDirection dir, string description, int daysAgo, Category? category = null,
            string? merchant = null, int? postedDaysAgo = null, bool pending = false, string[]? tags = null)
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

            if (tags is { Length: > 0 })
            {
                tx.ApplyAutoTags(tags, new HashSet<string>());
            }

            db.Transactions.Add(tx);
            return tx;
        }

        for (var m = 0; m < 7; m++)
        {
            Add(chk, 3000m + m, TransactionDirection.Credit, "ACME PAYROLL", m * 30 + 2, salary, "Acme");
            Add(chk, 250.17m + m, TransactionDirection.Credit, "BONUS", m * 30 + 9, bonus, "Acme", tags: ["work"]);
            Add(card, 59.99m, TransactionDirection.Debit, "NETFLIX", m * 30 + 5, restaurants, "Netflix", tags: ["fun"]);
        }

        Add(card, 4.50m, TransactionDirection.Debit, "STARBUCKS #1", 3, restaurants, "Starbucks");
        Add(card, 5.25m, TransactionDirection.Debit, "STARBUCKS #2", 9, restaurants, "STARBUCKS", tags: ["Trip"]);
        Add(card, 6.75m, TransactionDirection.Debit, "STARBUCKS #3", 15, restaurants, "starbucks");
        Add(card, 12.01m, TransactionDirection.Debit, "NO MERCHANT SHOP", 4, transport);
        Add(card, 13.02m, TransactionDirection.Debit, "BLANK MERCHANT SHOP", 6, transport, "   ");
        Add(chk, 80.03m, TransactionDirection.Debit, "LATE POSTING", 40, groceries, "Grocer", postedDaysAgo: 36);
        Add(chk, 22.04m, TransactionDirection.Debit, "STILL PENDING", 1, groceries, "Grocer", pending: true);

        Add(chk, 500m, TransactionDirection.Debit, "TO SAVINGS", 20);
        Add(sav, 500m, TransactionDirection.Credit, "FROM CHECKING", 18);
        Add(chk, 250m, TransactionDirection.Debit, "VISA PAYMENT", 30);
        Add(card, 250m, TransactionDirection.Debit, "VISA PAYMENT", 29);
        Add(chk, 300m, TransactionDirection.Debit, "TO HIDDEN", 12);
        Add(hidden, 300m, TransactionDirection.Credit, "FROM CHECKING", 11);
        Add(chk, 123.45m, TransactionDirection.Debit, "UNPAIRED DEBIT", 8, groceries, "Grocer", tags: ["trip"]);
        Add(chk, 90.05m, TransactionDirection.Debit, "CATEGORIZED TRANSFER LEG", 14, transfer);
        Add(chk, 400.06m, TransactionDirection.Debit, "BROKERAGE", 17, invest);
        Add(chk, 77.07m, TransactionDirection.Credit, "REFUND", 5, null, "Shop%_Co");
        Add(chk, 17.08m, TransactionDirection.Debit, "PERCENT MERCHANT", 6, groceries, "Shop%_Co");
        Add(chk, 18.09m, TransactionDirection.Debit, "OTHER MERCHANT", 6, groceries, "ShopXYCo");

        Add(hidden, 999.11m, TransactionDirection.Debit, "HIDDEN SPEND", 10, groceries, "Hiddenmart");
        Add(closed, 888.12m, TransactionDirection.Debit, "CLOSED SPEND", 10, groceries, "Closedmart");
        db.Transactions.Add(Transaction.CreatePlanned(tenantId, chk.AccountId, Usd(777m), TransactionDirection.Debit, "PLANNED RENT", Ago(2), TestClock.Provider));

        // Splits: parts in different categories of one top-level group (one transaction counts once there), a non-spending part, an uncategorized part.
        var split1 = Add(card, 100.13m, TransactionDirection.Debit, "BIG BOX", 7, groceries, "Big Box", tags: ["trip"]);
        split1.SetSplits([new TransactionSplit(Usd(60.13m), groceries.CategoryId), new TransactionSplit(Usd(40m), restaurants.CategoryId)]);
        var split2 = Add(card, 50.14m, TransactionDirection.Debit, "BIG BOX 2", 2, null, "Big Box");
        split2.SetSplits([new TransactionSplit(Usd(30.14m), invest.CategoryId), new TransactionSplit(Usd(20m), null)]);
        var split3 = Add(chk, 200m, TransactionDirection.Credit, "SPLIT INCOME", 3, salary, "Acme");
        split3.SetSplits([new TransactionSplit(Usd(150m), salary.CategoryId), new TransactionSplit(Usd(50m), bonus.CategoryId)]);

        var rng = new Random(7);
        string[] merchants = ["Shell", "Trader Joe's", "Amazon", "Uber", "Chipotle", "Target", "SHELL", "amazon"];
        Account[] spendAccounts = [chk, card, chk, card, hidden];
        for (var i = 0; i < randomRows; i++)
        {
            var merchant = merchants[rng.Next(merchants.Length)];
            // Distinct cents per row, so ranking never depends on tie-breaking.
            var amount = Math.Round(rng.Next(100, 12_000) / 100m + (i % 97) / 10_000m, 4);
            Add(spendAccounts[rng.Next(spendAccounts.Length)], amount, TransactionDirection.Debit, $"{merchant} #{i}", rng.Next(0, 210),
                spendCats[rng.Next(spendCats.Length)], merchant, tags: tagSets[rng.Next(tagSets.Length)]);
        }

        await db.SaveChangesAsync();
        return new Seeded(tenant, chk.AccountId.Value, card.AccountId.Value);
    }

    private static (GetSpendingBreakdownHandler Memory, GetSpendingBreakdownHandler Sql) Handlers(FlowLedgerDbContext db)
    {
        var accounts = new AccountRepository(db);
        var categories = new CategoryRepository(db);
        var repo = new TransactionRepository(db);
        return (
            new GetSpendingBreakdownHandler(repo, accounts, categories),
            new GetSpendingBreakdownHandler(repo, accounts, categories, new EfBreakdownStore(db), new EfTransactionAggregateStore(db)));
    }

    private static IEnumerable<(string Name, BreakdownQuery Query)> Queries(Seeded s)
    {
        var from = Ago(120);
        var to = Ago(0);
        foreach (var by in Enum.GetValues<BreakdownGroupBy>())
        {
            foreach (var income in new[] { false, true })
            {
                yield return ($"{by} income={income}", new BreakdownQuery(from, to, null, income, by));
            }

            yield return ($"{by} week buckets", new BreakdownQuery(Ago(60), to, null, false, by, Bucket: "week"));
            yield return ($"{by} day buckets", new BreakdownQuery(Ago(20), to, null, false, by, Bucket: "day"));
            yield return ($"{by} with non-spending", new BreakdownQuery(from, to, null, false, by, IncludeNonSpending: true));
            yield return ($"{by} one account", new BreakdownQuery(from, to, new HashSet<Guid> { s.Card }, false, by));
            yield return ($"{by} empty window", new BreakdownQuery(Ago(900), Ago(800), null, false, by));
        }

        yield return ("category drill Food", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Category, "Food"));
        yield return ("category select Food/Groceries", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Category, null, "Food/Groceries"));
        yield return ("category select Food", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Category, null, "Food"));
        yield return ("category select Uncategorized", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Category, null, "Uncategorized"));
        yield return ("category drill + select", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Category, "Food", "Food/Restaurants"));
        yield return ("tag select trip", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Tag, null, "TRIP"));
        yield return ("tag select untagged", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Tag, null, "Untagged"));
        yield return ("merchant select starbucks", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Merchant, null, "STARBUCKS"));
        yield return ("merchant select literal wildcard", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Merchant, null, "shop%_co"));
        yield return ("merchant select unknown", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Merchant, null, "Unknown merchant"));
        yield return ("account select card", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Account, null, s.Card.ToString()));
        yield return ("account select garbage", new BreakdownQuery(from, to, null, false, BreakdownGroupBy.Account, null, "not-a-guid"));
    }

    /// <summary>Case variants of a name are one group; the spelling shown may differ between the engines, so groups compare by lower-cased key.</summary>
    private static object Normalize(BreakdownResult r) => new
    {
        r.From,
        r.To,
        r.Income,
        r.GroupBy,
        r.Parent,
        r.Total,
        r.PreviousTotal,
        r.TransactionCount,
        r.TransfersExcluded,
        r.NonSpendingExcluded,
        r.Partial,
        Groups = r.Groups.OrderByDescending(g => g.Amount).ThenBy(g => g.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(g => new { Key = g.Key.ToLowerInvariant(), Label = g.Label.ToLowerInvariant(), g.Amount, g.Count, g.Percent, g.HasChildren }).ToList(),
        r.Trend,
        Transactions = r.Transactions.OrderByDescending(t => t.Amount).ThenByDescending(t => t.Date).ThenBy(t => t.Id).ToList(),
    };

    [DockerFact]
    public async Task The_sql_breakdown_equals_the_in_memory_breakdown_for_every_grouping_slice_and_window()
    {
        var seeded = await SeedAsync(randomRows: 500);
        await using var db = _fixture.CreateDbContext(seeded.Tenant);
        var (memory, sql) = Handlers(db);

        var checkedCases = 0;
        foreach (var (name, query) in Queries(seeded))
        {
            var expected = await memory.HandleAsync(query);
            var actual = await sql.HandleAsync(query);
            Normalize(actual).Should().BeEquivalentTo(Normalize(expected), o => o.WithStrictOrdering(), "case: {0}", name);
            checkedCases++;
        }

        checkedCases.Should().BeGreaterThanOrEqualTo(40);

        // Guard against a vacuous comparison: the seed really produces every section.
        var plain = await sql.HandleAsync(new BreakdownQuery(Ago(120), Ago(0)));
        plain.Groups.Should().HaveCountGreaterThan(2);
        plain.Trend.Should().NotBeEmpty();
        plain.Transactions.Should().NotBeEmpty();
        plain.TransfersExcluded.Should().BeGreaterThan(0);
        plain.NonSpendingExcluded.Should().BeGreaterThan(0);
        var food = await sql.HandleAsync(new BreakdownQuery(Ago(120), Ago(0), null, false, BreakdownGroupBy.Category));
        food.Groups.Single(g => g.Key == "Food").Count.Should().BeGreaterThan(0, "a split transaction with two Food parts counts once there");
    }

    [DockerFact]
    public async Task The_sql_breakdown_never_loads_the_history_and_issues_the_same_statements_whatever_the_volume()
    {
        var small = await QueryCountAsync(randomRows: 20);
        await _fixture.ResetAsync();
        var large = await QueryCountAsync(randomRows: 1500);

        small.Statements.Should().Be(large.Statements, "the number of queries does not grow with the number of transactions");
        large.Statements.Should().BeLessThan(40);
        large.TransactionReads.Where(sql => !sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(
                sql => sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) || sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
                    || sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) || sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase)
                    || sql.Contains("splits", StringComparison.OrdinalIgnoreCase),
                "every other read is the bounded transfer probe, the top-200 list, a count, a sum, or the (few) split transactions, never a history scan");
    }

    private async Task<(int Statements, List<string> TransactionReads)> QueryCountAsync(int randomRows)
    {
        var seeded = await SeedAsync(randomRows);
        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), seeded.Tenant);
        var (_, sql) = Handlers(db);
        lock (log.Sql)
        {
            log.Sql.Clear();
        }

        await sql.HandleAsync(new BreakdownQuery(Ago(120), Ago(0)));

        lock (log.Sql)
        {
            return (log.Sql.Count, log.Sql.Where(s => s.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).ToList());
        }
    }
}
