using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class AccountTests
{
    private static readonly TenantId TenantId = TenantId.New();
    private static readonly Money ZeroUsd = Money.Zero("USD");
    private static readonly Money ThousandUsd = new(1000m, "USD");

    [Fact]
    public void Create_valid_account_records_tenant_and_type()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);

        account.TenantId.Should().Be(TenantId);
        account.AccountType.Should().Be(AccountType.Checking);
    }

    [Fact]
    public void Create_empty_name_throws_EmptyStringException()
    {
        var act = () => Account.Create(TenantId, "  ", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void Create_trims_name_whitespace()
    {
        var account = Account.Create(TenantId, "  My Checking  ", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        account.Name.Should().Be("My Checking");
    }

    [Fact]
    public void UpdateBalance_sets_the_balance()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);

        account.UpdateBalance(ThousandUsd, FlowLedger.TestSupport.TestClock.Provider);

        account.CurrentBalance.Should().Be(ThousandUsd);
    }

    [Fact]
    public void UpdateBalance_currency_mismatch_throws()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        var eur = new Money(100m, "EUR");

        var act = () => account.UpdateBalance(eur, FlowLedger.TestSupport.TestClock.Provider);
        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Asset_account_cannot_have_negative_balance_without_credit_limit()
    {
        var account = Account.Create(TenantId, "Savings", AccountType.Savings, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        var negative = new Money(-100m, "USD");

        var act = () => account.UpdateBalance(negative, FlowLedger.TestSupport.TestClock.Provider);
        act.Should().Throw<InvalidBalanceException>();
    }

    [Fact]
    public void Provider_balance_may_be_negative_on_an_asset_account()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ThousandUsd, FlowLedger.TestSupport.TestClock.Provider);

        account.ApplyProviderBalance(new Money(-35m, "USD"), FlowLedger.TestSupport.TestClock.Provider);

        account.CurrentBalance.Amount.Should().Be(-35m);
    }

    [Fact]
    public void Provider_balance_in_another_currency_still_throws()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ThousandUsd, FlowLedger.TestSupport.TestClock.Provider);
        var act = () => account.ApplyProviderBalance(new Money(1m, "EUR"), FlowLedger.TestSupport.TestClock.Provider);
        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Asset_account_with_credit_limit_allows_negative_balance()
    {
        var creditLimit = new Money(5000m, "USD");
        var account = Account.Create(TenantId, "Credit Card", AccountType.CreditCard, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider, creditLimit);

        account.UpdateBalance(new Money(-200m, "USD"), FlowLedger.TestSupport.TestClock.Provider); // should not throw

        account.CurrentBalance.Amount.Should().Be(-200m);
    }

    [Fact]
    public void Rename_trims_and_updates_name()
    {
        var account = Account.Create(TenantId, "Old Name", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        account.Rename("  New Name  ");
        account.Name.Should().Be("New Name");
    }

    [Fact]
    public void Rename_empty_throws()
    {
        var account = Account.Create(TenantId, "Name", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        var act = () => account.Rename("");
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void Deactivate_sets_IsActive_to_false()
    {
        var account = Account.Create(TenantId, "Old", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        account.Deactivate();
        account.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Account_TenantId_is_preserved()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd, FlowLedger.TestSupport.TestClock.Provider);
        account.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public void Credit_limit_currency_mismatch_throws()
    {
        var act = () => Account.Create(
            TenantId, "Card", AccountType.CreditCard,
            Money.Zero("USD"), FlowLedger.TestSupport.TestClock.Provider, creditLimit: new Money(1000m, "EUR"));

        act.Should().Throw<CurrencyMismatchException>();
    }
}
