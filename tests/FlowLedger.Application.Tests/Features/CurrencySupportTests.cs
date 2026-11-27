using FlowLedger.Application.Features.Assets;
using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features;

/// <summary>#124: currency must come from the account, and aggregates must never silently add across currencies.</summary>
public sealed class CurrencySupportTests
{
    private static readonly TenantId Tenant = TenantId.From(FakeTenantContext.DefaultTenantId);
    private static readonly DateOnly Today = new(2026, 11, 13);

    [Fact]
    public async Task Csv_import_into_a_EUR_account_stores_EUR_rows()
    {
        var txRepo = new FakeTransactionRepository();
        var acctRepo = new FakeAccountRepository();
        var occRepo = new FakePlannedOccurrenceRepository();
        var account = Account.Create(Tenant, "Euro checking", AccountType.Checking, new Money(100m, "EUR"), FlowLedger.TestSupport.TestClock.Provider);
        await acctRepo.AddAsync(account);
        var handler = new ImportTransactionsHandler(txRepo, acctRepo, occRepo, new FakeTenantContext(), new MatchingEngine(occRepo), FlowLedger.TestSupport.TestClock.Provider);

        var summary = await handler.HandleAsync(new ImportTransactionsCommand(
            account.Id, "Date,Amount,Description\n2024-01-10,-12.50,Bakery", new CsvColumnMapping(0, 1, 2)));

        summary.ImportedCount.Should().Be(1);
        txRepo.All.Single().Amount.Currency.Code.Should().Be("EUR");
    }

    [Fact]
    public void Net_worth_over_USD_and_EUR_accounts_returns_per_currency_totals_not_a_raw_sum()
    {
        var usd = Account.Create(Tenant, "US", AccountType.Checking, new Money(1000m, "USD"), FlowLedger.TestSupport.TestClock.Provider);
        var eur = Account.Create(Tenant, "EU", AccountType.Checking, new Money(500m, "EUR"), FlowLedger.TestSupport.TestClock.Provider);

        var result = NetWorthCalculator.Compute([usd, eur], [], Today);

        result.MixedCurrencies.Should().BeTrue();
        result.ByCurrency.Should().HaveCount(2);
        result.ByCurrency!.Single(c => c.Currency == "USD").NetWorth.Should().Be(1000m);
        result.ByCurrency!.Single(c => c.Currency == "EUR").NetWorth.Should().Be(500m);
        result.NetWorth.Should().NotBe(1500m);
    }

    [Fact]
    public void Net_worth_with_a_single_currency_is_unchanged()
    {
        var result = NetWorthCalculator.Compute(
            [Account.Create(Tenant, "A", AccountType.Checking, new Money(1000m, "EUR"), FlowLedger.TestSupport.TestClock.Provider)], [], Today);

        result.MixedCurrencies.Should().BeFalse();
        result.NetWorth.Should().Be(1000m);
        result.Currency.Should().Be("EUR");
    }

    [Fact]
    public void Forecast_with_mixed_currencies_returns_a_warning_instead_of_silently_empty_aggregate()
    {
        var usd = AccountId.New();
        var eur = AccountId.New();
        var asOf = new DateOnly(2026, 1, 1);
        var flow = new ForecastFlowInput
        {
            FlowId = RecurringFlowId.New(),
            AccountId = usd,
            Amount = new Money(10m, "USD"),
            Direction = TransactionDirection.Debit,
            Pattern = RecurrencePattern.Monthly(5),
            FlowStart = asOf,
            Name = "Sub"
        };

        var result = new ForecastEngine().Run(new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, asOf.AddMonths(2)),
            StartingBalances = new Dictionary<AccountId, Money> { [usd] = new Money(100m, "USD"), [eur] = new Money(100m, "EUR") },
            AccountIds = [usd, eur],
            RecurringFlows = [flow]
        });

        result.AggregateSeries.Should().BeEmpty();
        result.Warnings.Should().ContainSingle().Which.Should().Contain("currenc");
        result.AccountSeries.Should().HaveCount(2);
    }
}
