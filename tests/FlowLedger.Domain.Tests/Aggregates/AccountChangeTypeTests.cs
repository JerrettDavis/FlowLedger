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
}
