using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Events;
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
    public void Create_valid_account_raises_AccountConnected_event()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd);

        account.DomainEvents.Should().ContainSingle(e => e is AccountConnected);
        var evt = (AccountConnected)account.DomainEvents[0];
        evt.TenantId.Should().Be(TenantId);
        evt.AccountType.Should().Be(AccountType.Checking);
    }

    [Fact]
    public void Create_empty_name_throws_EmptyStringException()
    {
        var act = () => Account.Create(TenantId, "  ", AccountType.Checking, ZeroUsd);
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void Create_trims_name_whitespace()
    {
        var account = Account.Create(TenantId, "  My Checking  ", AccountType.Checking, ZeroUsd);
        account.Name.Should().Be("My Checking");
    }

    [Fact]
    public void UpdateBalance_raises_AccountBalanceUpdated_event()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd);
        account.ClearDomainEvents();

        account.UpdateBalance(ThousandUsd);

        account.DomainEvents.Should().ContainSingle(e => e is AccountBalanceUpdated);
        var evt = (AccountBalanceUpdated)account.DomainEvents[0];
        evt.PreviousBalance.Should().Be(ZeroUsd);
        evt.NewBalance.Should().Be(ThousandUsd);
    }

    [Fact]
    public void UpdateBalance_currency_mismatch_throws()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd);
        var eur = new Money(100m, "EUR");

        var act = () => account.UpdateBalance(eur);
        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Asset_account_cannot_have_negative_balance_without_credit_limit()
    {
        var account = Account.Create(TenantId, "Savings", AccountType.Savings, ZeroUsd);
        var negative = new Money(-100m, "USD");

        var act = () => account.UpdateBalance(negative);
        act.Should().Throw<InvalidBalanceException>();
    }

    [Fact]
    public void Asset_account_with_credit_limit_allows_negative_balance()
    {
        var creditLimit = new Money(5000m, "USD");
        var account = Account.Create(TenantId, "Credit Card", AccountType.CreditCard, ZeroUsd, creditLimit);

        account.UpdateBalance(new Money(-200m, "USD")); // should not throw

        account.CurrentBalance.Amount.Should().Be(-200m);
    }

    [Fact]
    public void Rename_trims_and_updates_name()
    {
        var account = Account.Create(TenantId, "Old Name", AccountType.Checking, ZeroUsd);
        account.Rename("  New Name  ");
        account.Name.Should().Be("New Name");
    }

    [Fact]
    public void Rename_empty_throws()
    {
        var account = Account.Create(TenantId, "Name", AccountType.Checking, ZeroUsd);
        var act = () => account.Rename("");
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void Deactivate_sets_IsActive_to_false()
    {
        var account = Account.Create(TenantId, "Old", AccountType.Checking, ZeroUsd);
        account.Deactivate();
        account.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Account_TenantId_is_preserved()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd);
        account.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public void ClearDomainEvents_empties_event_list()
    {
        var account = Account.Create(TenantId, "Checking", AccountType.Checking, ZeroUsd);
        account.DomainEvents.Should().NotBeEmpty();
        account.ClearDomainEvents();
        account.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Credit_limit_currency_mismatch_throws()
    {
        var act = () => Account.Create(
            TenantId, "Card", AccountType.CreditCard,
            Money.Zero("USD"), creditLimit: new Money(1000m, "EUR"));

        act.Should().Throw<CurrencyMismatchException>();
    }
}
