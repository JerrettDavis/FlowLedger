using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class AccountChangeTypeTests
{
    private static readonly TenantId Tenant = TenantId.New();

    [Fact]
    public void Reclassifies_checking_to_credit_card()
    {
        var a = Account.Create(Tenant, "Card", AccountType.Checking, Money.Zero("USD"));
        a.ChangeType(AccountType.CreditCard).Should().BeTrue();
        a.AccountType.Should().Be(AccountType.CreditCard);
    }

    [Fact]
    public void Same_type_is_a_noop()
    {
        var a = Account.Create(Tenant, "Chk", AccountType.Checking, Money.Zero("USD"));
        a.ChangeType(AccountType.Checking).Should().BeFalse();
    }

    [Fact]
    public void Refuses_asset_type_for_negative_balance_without_credit_limit()
    {
        var a = Account.Create(Tenant, "Card", AccountType.CreditCard, new Money(-50m, "USD"));
        a.ChangeType(AccountType.Checking).Should().BeFalse();
        a.AccountType.Should().Be(AccountType.CreditCard);
    }

    [Theory]
    [InlineData(AccountType.CreditCard, true)]
    [InlineData(AccountType.Loan, true)]
    [InlineData(AccountType.Mortgage, true)]
    [InlineData(AccountType.Checking, false)]
    [InlineData(AccountType.Investment, false)]
    public void Liability_classification(AccountType type, bool liability) =>
        Account.IsLiabilityType(type).Should().Be(liability);

    // ── Manual (user) override ─────────────────────────────────────────────────

    [Fact]
    public void Manual_change_pins_the_type_against_provider_inference()
    {
        var a = Account.Create(Tenant, "J. DAVIS", AccountType.Checking, new Money(45.60m, "USD"));

        a.SetTypeManually(AccountType.CreditCard).Should().BeTrue();

        a.TypeLockedByUser.Should().BeTrue();
        a.ChangeType(AccountType.Checking).Should().BeFalse("a pinned type must survive provider sync");
        a.AccountType.Should().Be(AccountType.CreditCard);
    }

    [Fact]
    public void Manual_change_to_a_liability_flips_a_negative_balance_to_amount_owed()
    {
        var a = Account.Create(Tenant, "J. DAVIS", AccountType.Checking, new Money(-45.60m, "USD"));

        a.SetTypeManually(AccountType.CreditCard);

        a.CurrentBalance.Amount.Should().Be(45.60m);
    }

    [Fact]
    public void Manual_change_with_the_same_type_still_pins_it()
    {
        var a = Account.Create(Tenant, "Chk", AccountType.Checking, Money.Zero("USD"));

        a.SetTypeManually(AccountType.Checking).Should().BeFalse();

        a.TypeLockedByUser.Should().BeTrue();
    }

    [Fact]
    public void Manual_change_to_an_asset_type_is_rejected_for_a_negative_balance()
    {
        var a = Account.Create(Tenant, "Card", AccountType.CreditCard, new Money(-50m, "USD"));

        var act = () => a.SetTypeManually(AccountType.Checking);

        act.Should().Throw<FlowLedger.Domain.Exceptions.InvalidBalanceException>();
        a.AccountType.Should().Be(AccountType.CreditCard);
        a.TypeLockedByUser.Should().BeFalse();
    }

    [Fact]
    public void Releasing_the_lock_lets_provider_inference_update_the_type_again()
    {
        var a = Account.Create(Tenant, "Chk", AccountType.Checking, Money.Zero("USD"));
        a.SetTypeManually(AccountType.Savings);

        a.ReleaseTypeLock();

        a.ChangeType(AccountType.CreditCard).Should().BeTrue();
    }
}
