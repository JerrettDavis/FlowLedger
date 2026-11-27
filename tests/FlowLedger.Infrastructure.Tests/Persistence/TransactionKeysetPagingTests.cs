using System.Data.Common;
using FlowLedger.Application.Abstractions;
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

/// <summary>Keyset paging walks the list in exactly the order the offset list uses, with no duplicates or gaps and no OFFSET (#160).</summary>
[Collection("Integration")]
public sealed class TransactionKeysetPagingTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public TransactionKeysetPagingTests(IntegrationTestFixture fixture) => _fixture = fixture;

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

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TestClock.Instant;
    }

    /// <summary>Each call is a millisecond later, so rows created with it get distinct created-at values.</summary>
    private sealed class TickingTime : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => TestClock.Instant.AddMilliseconds(Interlocked.Increment(ref _ticks));
    }

    private async Task<TestTenantContext> SeedAsync(int count)
    {
        var tenant = TestTenantContext.New();
        var account = await _fixture.SeedAccountAsync(tenant);
        var other = await _fixture.SeedAccountAsync(tenant, "Other");
        await using var db = _fixture.CreateDbContext(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        var ticking = new TickingTime();
        for (var i = 0; i < count; i++)
        {
            // A handful of dates, and (for the first half) one shared created-at: the id is the last tie-break and must page correctly.
            var date = new DateOnly(2026, 1, 1).AddDays(i % 9);
            TimeProvider time = i < count / 2 ? new FixedTime() : ticking;
            db.Transactions.Add(Transaction.RecordActual(
                tenantId, i % 5 == 0 ? other : account, new Money(1m + i, Currency.Usd), TransactionDirection.Debit,
                i % 4 == 0 ? $"Coffee {i}" : $"Rent {i}", date, date, TransactionSource.Manual, time: time));
        }

        await db.SaveChangesAsync();
        return tenant;
    }

    private static async Task<List<Transaction>> WalkAsync(
        ITransactionRepository repo, int pageSize, bool descending, AccountId? account = null, string? search = null)
    {
        var all = new List<Transaction>();
        ITransactionRepository.TransactionKeyset? after = null;
        while (true)
        {
            var page = await repo.ListAfterAsync(after, account, null, null, pageSize, descending, everydayOnly: true, search);
            all.AddRange(page);
            if (page.Count < pageSize)
            {
                return all;
            }

            after = new ITransactionRepository.TransactionKeyset(page[^1].EffectiveDate, page[^1].CreatedAt, page[^1].Id);
        }
    }

    [DockerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Walking_the_keyset_pages_yields_the_offset_list_exactly(bool descending)
    {
        var tenant = await SeedAsync(1100);
        await using var db = _fixture.CreateDbContext(tenant);
        var repo = new TransactionRepository(db);

        var expected = await repo.ListAsync(null, null, null, 0, int.MaxValue, default, everydayOnly: true, sort: TransactionSortKey.Date, descending: descending);
        var walked = await WalkAsync(repo, 97, descending);

        walked.Should().HaveCount(1100);
        walked.Select(t => t.Id).Should().Equal(expected.Select(t => t.Id), "same total order, no duplicate and no gap across pages");
    }

    [DockerFact]
    public async Task Keyset_paging_honours_the_filters_and_issues_no_offset()
    {
        var tenant = await SeedAsync(300);
        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);
        var repo = new TransactionRepository(db);

        var expected = await repo.ListAsync(null, null, null, 0, int.MaxValue, default, everydayOnly: true, search: "coffee", sort: TransactionSortKey.Date, descending: true);
        log.Sql.Clear();
        var walked = await WalkAsync(repo, 20, descending: true, search: "coffee");

        walked.Select(t => t.Id).Should().Equal(expected.Select(t => t.Id));
        walked.Should().OnlyContain(t => t.Description.StartsWith("Coffee", StringComparison.Ordinal));
        log.Sql.Where(s => s.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).Should().NotBeEmpty()
            .And.OnlyContain(s => !s.Contains("OFFSET", StringComparison.OrdinalIgnoreCase) && s.Contains("LIMIT", StringComparison.OrdinalIgnoreCase),
                "each page is an index range with a LIMIT, never an OFFSET that rescans the rows before it");
    }
}
