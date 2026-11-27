using System.Data.Common;
using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>The balance-history read is bounded by a date window and issues a fixed number of queries (#160).</summary>
[Collection("Integration")]
public sealed class BalanceHistoryReaderTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public BalanceHistoryReaderTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TestClock.Instant;
    }

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

    [DockerFact]
    public async Task Windowed_read_returns_only_the_window_plus_one_anchor_per_account_in_two_queries()
    {
        var tenant = TestTenantContext.New();
        var a = await _fixture.SeedAccountAsync(tenant, "A");
        var b = await _fixture.SeedAccountAsync(tenant, "B");
        var today = TestClock.Today;
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            // 800 days of history per account, two snapshots on the most recent day (the later one must win).
            foreach (var account in new[] { a.Value, b.Value })
            {
                for (var d = 0; d < 800; d++)
                {
                    var at = new DateTimeOffset(today.AddDays(-d).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
                    seed.BalanceSnapshots.Add(AccountBalanceSnapshotRecord.Create(tenant.TenantId, account, "Simulated", "p", 100m + d, "USD", at));
                }

                seed.BalanceSnapshots.Add(AccountBalanceSnapshotRecord.Create(
                    tenant.TenantId, account, "Simulated", "p", 7m, "USD", new DateTimeOffset(today.ToDateTime(new TimeOnly(18, 0)), TimeSpan.Zero)));
            }

            await seed.SaveChangesAsync();
        }

        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);

        var from = today.AddDays(-30);
        var points = await new BalanceHistoryReader(db).DailyAccountBalancesAsync(from, null);

        log.Sql.Should().HaveCount(2, "one windowed read plus one anchor read, however much history exists");
        log.Sql.Should().OnlyContain(sql => sql.Contains("captured_at", StringComparison.OrdinalIgnoreCase), "the date bound is applied in SQL");
        points.Where(p => p.Date >= from).Should().HaveCount(31 * 2);
        points.Where(p => p.Date < from).Should().HaveCount(2, "exactly one anchor per account");
        points.Should().HaveCountLessThan(800, "the 800-day history is not loaded");
        points.Single(p => p.AccountId == a.Value && p.Date == today).Balance.Should().Be(7m, "the last snapshot of the day wins");
        points.Where(p => p.Date < from).Select(p => p.Date).Should().OnlyContain(d => d == from.AddDays(-1));
    }

    [DockerFact]
    public async Task Many_snapshots_per_day_are_collapsed_to_one_per_account_per_day_in_sql()
    {
        var tenant = TestTenantContext.New();
        var accounts = new[] { (await _fixture.SeedAccountAsync(tenant, "A")).Value, (await _fixture.SeedAccountAsync(tenant, "B")).Value };
        var today = TestClock.Today;
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            // 2 accounts x 10 days x 1,000 snapshots = 20,000 rows; the balance is the snapshot's index so the last one of a day is known.
            foreach (var account in accounts)
            {
                for (var d = 0; d < 10; d++)
                {
                    for (var i = 0; i < 1000; i++)
                    {
                        var at = new DateTimeOffset(today.AddDays(-d).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddSeconds(i * 86);
                        seed.BalanceSnapshots.Add(AccountBalanceSnapshotRecord.Create(tenant.TenantId, account, "Simulated", "p", i, "USD", at));
                    }
                }
            }

            await seed.SaveChangesAsync();
        }

        var log = new CommandLog();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log).Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);

        var points = await new BalanceHistoryReader(db).DailyAccountBalancesAsync(today.AddDays(-9), null);

        log.Sql.Should().HaveCount(2);
        log.Sql[0].ToLowerInvariant().Should().Contain("max(", "the per-day collapse is a SQL aggregate, not a client-side grouping over every snapshot");
        points.Should().HaveCount(2 * 10);
        points.Should().OnlyContain(p => p.Balance == 999m, "the last snapshot of each day wins");
    }

    [DockerFact]
    public async Task Default_read_is_bounded_to_the_default_window()
    {
        var tenant = TestTenantContext.New();
        var a = await _fixture.SeedAccountAsync(tenant, "A");
        var today = TestClock.Today;
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            for (var d = 0; d < 900; d += 3)
            {
                var at = new DateTimeOffset(today.AddDays(-d).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
                seed.BalanceSnapshots.Add(AccountBalanceSnapshotRecord.Create(tenant.TenantId, a.Value, "Simulated", "p", d, "USD", at));
            }

            await seed.SaveChangesAsync();
        }

        await using var db = _fixture.CreateDbContext(tenant);
        var points = await new BalanceHistoryReader(db, new FixedTime()).DailyAccountBalancesAsync();

        var oldest = today.AddDays(-IBalanceHistoryReader.DefaultWindowDays);
        points.Count(p => p.Date < oldest).Should().Be(1, "only the anchor lies before the window");
        points.Should().HaveCountLessThan(900 / 3, "history older than the window is not returned");
    }
}
