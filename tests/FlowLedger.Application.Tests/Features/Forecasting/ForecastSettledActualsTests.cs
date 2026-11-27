using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Forecasting;

/// <summary>
/// The starting balance is the provider balance as of AsOf, so actuals already settled on or before AsOf are
/// already inside it and must not be applied a second time (#117).
/// </summary>
public sealed class ForecastSettledActualsTests
{
    private static readonly Currency Usd = new("USD");
    private static readonly TenantId Tenant = TenantId.From(FakeTenantContext.DefaultTenantId);
    private static readonly DateOnly Today = new(2026, 11, 25);

    private static ForecastTransactionInput Actual(AccountId account, DateOnly date, TransactionStatus status, decimal amount = 60m)
        => new()
        {
            TransactionId = TransactionId.New(),
            AccountId = account,
            Amount = new Money(amount, Usd),
            Direction = TransactionDirection.Debit,
            EffectiveDate = date,
            Status = status,
            Description = "Charge"
        };

    private static ForecastRequest Request(AccountId account, params ForecastTransactionInput[] actuals) => new()
    {
        AsOf = Today,
        Horizon = new DateOnlyRange(Today, Today.AddDays(30)),
        StartingBalances = new Dictionary<AccountId, Money> { [account] = new Money(1000m, Usd) },
        AccountIds = [account],
        RecurringFlows = [],
        ActualTransactions = actuals
    };

    [Fact]
    public void Posted_actual_dated_today_is_not_applied_on_top_of_a_balance_that_already_includes_it()
    {
        var account = AccountId.New();

        var result = new ForecastEngine().Run(Request(account, Actual(account, Today, TransactionStatus.Posted)));

        result.AccountSeries.Single().Points.Should().BeEmpty();
        result.AggregateSeries.Should().NotContain(p => p.Balance.Amount == 940m);
    }

    [Fact]
    public void Pending_actual_is_still_applied_because_the_balance_does_not_yet_include_it()
    {
        var account = AccountId.New();

        var result = new ForecastEngine().Run(Request(account, Actual(account, Today, TransactionStatus.Pending)));

        result.AccountSeries.Single().Points.Single().Balance.Amount.Should().Be(940m);
    }

    [Fact]
    public void Pending_actual_dated_before_asof_is_applied_on_the_first_horizon_day()
    {
        var account = AccountId.New();

        var result = new ForecastEngine().Run(Request(account, Actual(account, Today.AddDays(-2), TransactionStatus.Pending)));

        var point = result.AccountSeries.Single().Points.Single();
        point.Date.Should().Be(Today);
        point.Balance.Amount.Should().Be(940m);
    }

    [Fact]
    public void Actual_dated_after_asof_is_applied()
    {
        var account = AccountId.New();

        var result = new ForecastEngine().Run(Request(account, Actual(account, Today.AddDays(3), TransactionStatus.Posted)));

        result.AccountSeries.Single().Points.Single().Balance.Amount.Should().Be(940m);
    }

    [Fact]
    public async Task Handler_clamps_from_before_asof_so_history_is_not_reapplied()
    {
        var accounts = new FakeAccountRepository();
        var txs = new FakeTransactionRepository();
        var account = Account.Create(Tenant, "Checking", AccountType.Checking, new Money(1000m, Usd), FlowLedger.TestSupport.TestClock.Provider);
        await accounts.AddAsync(account);
        foreach (var offset in new[] { 0, -5, -20 })
        {
            await txs.AddAsync(Transaction.RecordActual(Tenant, account.AccountId, new Money(60m, Usd),
                TransactionDirection.Debit, $"Charge {offset}", Today.AddDays(offset), Today.AddDays(offset), TransactionSource.CsvImport, FlowLedger.TestSupport.TestClock.Provider));
        }

        var handler = new GetForecastHandler(accounts, new FakeRecurringFlowRepository(), txs, new ForecastEngine(), new FakeTenantContext());

        var defaultRun = await handler.HandleAsync(new GetForecastQuery { AsOf = Today, Months = 1 });
        var earlierFrom = await handler.HandleAsync(new GetForecastQuery { AsOf = Today, HorizonStart = Today.AddDays(-30), Months = 1 });

        defaultRun.AccountSeries.Single().Points.Should().BeEmpty();
        earlierFrom.Horizon.Start.Should().Be(Today);
        earlierFrom.AccountSeries.Single().Points.Should().BeEmpty();
        earlierFrom.AggregateSeries.Should().BeEquivalentTo(defaultRun.AggregateSeries);
    }
}
