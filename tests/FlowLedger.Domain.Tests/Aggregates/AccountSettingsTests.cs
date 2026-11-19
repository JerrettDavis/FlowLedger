using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class AccountSettingsTests
{
    private static readonly TenantId Tenant = TenantId.New();

    [Fact]
    public void New_checking_account_is_a_visible_standard_account()
    {
        var a = Account.Create(Tenant, "Chk", AccountType.Checking, Money.Zero("USD"));
        a.Mode.Should().Be(AccountMode.Standard);
        a.HideTransactions.Should().BeFalse();
        a.TransactionsHidden.Should().BeFalse();
        a.IsActive.Should().BeTrue();
    }

    [Fact]
    public void New_investment_account_defaults_to_a_holding_with_hidden_transactions()
    {
        var a = Account.Create(Tenant, "Brokerage", AccountType.Investment, Money.Zero("USD"));
        a.Mode.Should().Be(AccountMode.Holding);
        a.TransactionsHidden.Should().BeTrue();
    }

    [Fact]
    public void Hide_flag_hides_transactions_of_a_standard_account()
    {
        var a = Account.Create(Tenant, "Chk", AccountType.Checking, Money.Zero("USD"));
        a.ApplySettings(true, AccountMode.Standard, hideTransactions: true);
        a.TransactionsHidden.Should().BeTrue();
    }

    [Fact]
    public void Settings_can_disable_and_reenable()
    {
        var a = Account.Create(Tenant, "Chk", AccountType.Checking, Money.Zero("USD"));
        a.ApplySettings(false, AccountMode.Standard, false);
        a.IsActive.Should().BeFalse();
        a.ApplySettings(true, AccountMode.Standard, false);
        a.IsActive.Should().BeTrue();
    }
}
