using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Forecasting;

/// <summary>#69: the default forecast covers liquid accounts only, so a mortgage, card or brokerage never drags "Forecast Low" below zero.</summary>
public sealed class ForecastLiquidAccountsTests
{
    private static readonly Currency Usd = new("USD");
    private static readonly TenantId Tenant = TenantId.From(FakeTenantContext.DefaultTenantId);
    private static readonly DateOnly Today = new(2026, 11, 25);

    private static GetForecastHandler Handler(FakeAccountRepository accounts)
        => new(accounts, new FakeRecurringFlowRepository(), new FakeTransactionRepository(), new ForecastEngine(), new FakeTenantContext());

    private static async Task<FakeAccountRepository> SeedAsync(params (string Name, AccountType Type, decimal Balance)[] rows)
    {
        var repo = new FakeAccountRepository();
        foreach (var (name, type, balance) in rows)
        {
            await repo.AddAsync(Account.Create(Tenant, name, type, new Money(balance, Usd)));
        }

        return repo;
    }

    [Fact]
    public async Task Default_forecast_includes_checking_savings_and_cash_but_not_loans_cards_or_investments()
    {
        var accounts = await SeedAsync(
            ("Checking", AccountType.Checking, 1000m), ("Savings", AccountType.Savings, 500m), ("Wallet", AccountType.Cash, 50m),
            ("Mortgage", AccountType.Mortgage, 250000m), ("Card", AccountType.CreditCard, 300m), ("Brokerage", AccountType.Investment, 9000m));

        var result = await Handler(accounts).HandleAsync(new GetForecastQuery { AsOf = Today, Months = 1 });

        result.AccountSeries.Should().HaveCount(3);
        result.AccountSeries.Sum(s => s.StartingBalance.Amount).Should().Be(1550m, "only checking + savings + cash are summed");
    }

    [Fact]
    public async Task A_big_mortgage_balance_is_not_forecast_alongside_checking()
    {
        var accounts = await SeedAsync(("Checking", AccountType.Checking, 1200m), ("Mortgage", AccountType.Mortgage, 250000m));

        var result = await Handler(accounts).HandleAsync(new GetForecastQuery { AsOf = Today, Months = 1 });

        result.AccountSeries.Should().ContainSingle().Which.StartingBalance.Amount.Should().Be(1200m);
        result.OverdraftWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_liquid_account_the_forecast_falls_back_to_every_account()
    {
        var accounts = await SeedAsync(("Mortgage", AccountType.Mortgage, 1000m), ("Brokerage", AccountType.Investment, 500m));

        var result = await Handler(accounts).HandleAsync(new GetForecastQuery { AsOf = Today, Months = 1 });

        result.AccountSeries.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_explicit_account_selection_overrides_the_liquid_default()
    {
        var accounts = await SeedAsync(("Checking", AccountType.Checking, 1000m), ("Brokerage", AccountType.Investment, 9000m));
        var brokerage = (await accounts.ListAsync()).Single(a => a.AccountType == AccountType.Investment);

        var result = await Handler(accounts).HandleAsync(new GetForecastQuery { AsOf = Today, Months = 1, AccountIds = [brokerage.AccountId.Value] });

        result.AccountSeries.Should().ContainSingle();
        result.AccountSeries.Single().StartingBalance.Amount.Should().Be(9000m);
    }
}
