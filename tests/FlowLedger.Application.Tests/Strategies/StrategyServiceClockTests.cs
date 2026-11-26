using FlowLedger.Application.Features.Strategies;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Strategies;

/// <summary>#127 first slice: StrategyService reads "now" from an injected <see cref="TimeProvider"/>.</summary>
public sealed class StrategyServiceClockTests
{
    private static readonly Currency Usd = new("USD");
    private static readonly TenantId Tenant = TenantId.From(FakeTenantContext.DefaultTenantId);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoRates : IAccountRateStore
    {
        public Task<IReadOnlyList<AccountRate>> ListAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccountRate>>([]);

        public Task UpsertAsync(Guid tenantId, AccountRate rate, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(Guid tenantId, Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<StrategyReport> ReportAsync(DateTimeOffset now)
    {
        var accounts = new FakeAccountRepository();
        var txs = new FakeTransactionRepository();
        var account = Account.Create(Tenant, "Checking", AccountType.Checking, new Money(5000m, Usd));
        await accounts.AddAsync(account);
        await txs.AddAsync(Transaction.RecordActual(Tenant, account.AccountId, new Money(900m, Usd), TransactionDirection.Debit,
            "Rent", new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15), TransactionSource.CsvImport));

        var service = new StrategyService(
            txs, accounts, new FakeCategoryRepository(), new StaticMarketRateProvider(), new NoRates(), new FakeTenantContext(),
            llm: null, time: new FixedTime(now));
        return await service.GetReportAsync();
    }

    [Fact]
    public async Task Report_is_stamped_with_the_injected_clock()
    {
        var now = new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero);

        var report = await ReportAsync(now);

        report.GeneratedAt.Should().Be(now);
    }

    [Fact]
    public async Task November_counts_as_a_complete_month_only_once_the_clock_reaches_December()
    {
        var inNovember = await ReportAsync(new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.Zero));
        var inDecember = await ReportAsync(new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero));

        inNovember.AvgMonthlySpend.Should().Be(0m);
        inDecember.AvgMonthlySpend.Should().BeGreaterThan(0m);
    }
}
