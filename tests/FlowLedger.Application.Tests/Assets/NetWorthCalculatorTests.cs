using FlowLedger.Application.Features.Assets;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Assets;

public sealed class NetWorthCalculatorTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly DateOnly Today = new(2026, 11, 13);

    private static Account Acct(AccountType t, decimal bal) => Account.Create(Tenant, t.ToString(), t, new Money(bal, "USD"));

    private static Asset House(decimal value, Account? link = null)
    {
        var a = Asset.Create(Tenant, "House", AssetCategory.RealEstate, new Money(value, "USD"), Today);
        a.LinkAccount(link);
        return a;
    }

    [Fact]
    public void Accounts_only()
    {
        var r = NetWorthCalculator.Compute([Acct(AccountType.Checking, 1000m), Acct(AccountType.CreditCard, 300m)], [], Today);
        r.NetWorth.Should().Be(700m);
        r.PhysicalAssets.Should().Be(0m);
    }

    [Fact]
    public void Overpaid_card_increases_net_worth()
    {
        var r = NetWorthCalculator.Compute([Acct(AccountType.Checking, 1000m), Acct(AccountType.CreditCard, -200m)], [], Today);
        r.NetWorth.Should().Be(1200m);
    }

    [Fact]
    public void Unlinked_asset_is_added()
        => NetWorthCalculator.Compute([Acct(AccountType.Checking, 100m)], [House(900m)], Today).NetWorth.Should().Be(1000m);

    [Fact]
    public void Manual_asset_account_link_is_not_double_counted()
    {
        var manual = Acct(AccountType.ManualAsset, 900m);
        var r = NetWorthCalculator.Compute([manual], [House(900m, manual)], Today);
        r.NetWorth.Should().Be(900m);
        r.CarriedByAccounts.Should().Be(900m);
        r.PhysicalAssets.Should().Be(0m);
    }

    [Fact]
    public void Mortgage_link_counts_asset_once_and_liability_once()
    {
        var mortgage = Acct(AccountType.Mortgage, 600m);
        var r = NetWorthCalculator.Compute([mortgage], [House(900m, mortgage)], Today);
        r.NetWorth.Should().Be(300m);
    }

    [Fact]
    public void Inactive_linked_account_is_treated_as_unlinked()
    {
        var manual = Acct(AccountType.ManualAsset, 900m);
        manual.Deactivate();
        var r = NetWorthCalculator.Compute([manual], [House(900m, manual)], Today);
        r.PhysicalAssets.Should().Be(900m);
        r.NetWorth.Should().Be(900m);
    }

    [Fact]
    public void Inactive_asset_is_ignored_and_estimate_is_used_not_anchor()
    {
        var gone = House(500m);
        gone.Deactivate();
        var dep = Asset.Create(Tenant, "Phone", AssetCategory.Electronics, new Money(1000m, "USD"), Today.AddYears(-1));
        dep.SetValuationModel(ValuationMethod.DecliningBalance, 0.5m, null, null);

        var r = NetWorthCalculator.Compute([], [gone, dep], Today);
        r.PhysicalAssets.Should().BeApproximately(500m, 2m);
    }
}
