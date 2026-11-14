using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class AssetTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly DateOnly Jan1 = new(2024, 1, 1);

    private static Asset NewAsset(decimal value = 1000m, AssetCategory cat = AssetCategory.Electronics)
        => Asset.Create(Tenant, "Laptop", cat, new Money(value, "USD"), Jan1);

    [Fact]
    public void Create_records_initial_valuation_and_raises_event()
    {
        var a = NewAsset();
        a.Valuations.Should().ContainSingle().Which.Amount.Should().Be(1000m);
        a.DomainEvents.Should().ContainSingle(e => e is AssetAdded);
    }

    [Fact]
    public void Create_rejects_blank_name_and_negative_value()
    {
        var blank = () => Asset.Create(Tenant, " ", AssetCategory.Other, new Money(1m, "USD"), Jan1);
        var neg = () => Asset.Create(Tenant, "x", AssetCategory.Other, new Money(-1m, "USD"), Jan1);
        blank.Should().Throw<EmptyStringException>();
        neg.Should().Throw<InvalidAssetException>();
    }

    [Fact]
    public void RecordValuation_newer_updates_current_and_older_only_history()
    {
        var a = NewAsset();
        a.RecordValuation(new Money(800m, "USD"), Jan1.AddMonths(6), "Manual");
        a.CurrentValue.Amount.Should().Be(800m);
        a.ValuedOn.Should().Be(Jan1.AddMonths(6));

        a.RecordValuation(new Money(1200m, "USD"), Jan1.AddMonths(-3), "Manual");
        a.CurrentValue.Amount.Should().Be(800m);
        a.Valuations.Should().HaveCount(3);
    }

    [Fact]
    public void RecordValuation_rejects_currency_mismatch()
    {
        var a = NewAsset();
        var act = () => a.RecordValuation(new Money(5m, "EUR"), Jan1, "Manual");
        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void UpdateDetails_cleans_attributes_and_enforces_cap()
    {
        var a = NewAsset();
        a.UpdateDetails("Rifle", AssetCategory.Firearm, " Ruger ", "10/22", "SN-1", " ", null, null,
            new Dictionary<string, string> { ["Caliber"] = " .22 ", ["Empty"] = " ", [" "] = "x" });
        a.Manufacturer.Should().Be("Ruger");
        a.Notes.Should().BeNull();
        a.Attributes.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("Caliber", ".22"));

        var many = Enumerable.Range(0, Asset.MaxAttributes + 1).ToDictionary(i => $"k{i}", _ => "v");
        var act = () => a.UpdateDetails("x", AssetCategory.Other, null, null, null, null, null, null, many);
        act.Should().Throw<InvalidAssetException>();
    }

    [Fact]
    public void SetValuationModel_validates()
    {
        var a = NewAsset();
        ((Action)(() => a.SetValuationModel(ValuationMethod.DecliningBalance, 1.5m, null, null))).Should().Throw<InvalidAssetException>();
        ((Action)(() => a.SetValuationModel(ValuationMethod.StraightLine, 0m, null, null))).Should().Throw<InvalidAssetException>();
        ((Action)(() => a.SetValuationModel(ValuationMethod.StraightLine, 0m, -1m, 5))).Should().Throw<InvalidAssetException>();
    }

    // ── Estimator ────────────────────────────────────────────────────────────

    [Fact]
    public void LinkAccount_rejects_other_tenants_account_and_allows_unlink()
    {
        var a = NewAsset();
        var mine = Account.Create(Tenant, "Mortgage", AccountType.Mortgage, Money.Zero("USD"));
        var theirs = Account.Create(TenantId.New(), "Other", AccountType.Mortgage, Money.Zero("USD"));

        a.LinkAccount(mine);
        a.LinkedAccountId.Should().Be(mine.Id);
        ((Action)(() => a.LinkAccount(theirs))).Should().Throw<InvalidAssetException>();
        a.LinkedAccountId.Should().Be(mine.Id);
        a.LinkAccount(null);
        a.LinkedAccountId.Should().BeNull();
    }

    [Fact]
    public void Estimate_manual_returns_anchor()
    {
        var a = NewAsset();
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(5)).Amount.Should().Be(1000m);
    }

    [Fact]
    public void Estimate_before_anchor_returns_anchor()
    {
        var a = NewAsset();
        a.SetValuationModel(ValuationMethod.DecliningBalance, 0.2m, null, null);
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(-1)).Amount.Should().Be(1000m);
    }

    [Fact]
    public void Estimate_declining_balance_compounds()
    {
        var a = NewAsset();
        a.SetValuationModel(ValuationMethod.DecliningBalance, 0.2m, null, null);
        // ~2 years at 20%/yr → 640 (leap-day makes it fractionally under 2y)
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(2)).Amount.Should().BeApproximately(640m, 1m);
    }

    [Fact]
    public void Estimate_appreciation_grows()
    {
        var a = NewAsset(100000m, AssetCategory.RealEstate);
        a.SetValuationModel(ValuationMethod.Appreciation, 0.04m, null, null);
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(1)).Amount.Should().BeApproximately(104000m, 100m);
    }

    [Fact]
    public void Estimate_rate_zero_is_flat()
    {
        var a = NewAsset();
        a.SetValuationModel(ValuationMethod.Appreciation, 0m, null, null);
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(3)).Amount.Should().Be(1000m);
    }

    [Fact]
    public void Estimate_declining_rate_of_100_percent_is_zero_not_negative()
    {
        var a = NewAsset();
        a.SetValuationModel(ValuationMethod.DecliningBalance, 1m, null, null);
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(1)).Amount.Should().Be(0m);
    }

    [Fact]
    public void Estimate_straight_line_floors_at_salvage_past_useful_life()
    {
        var a = NewAsset(1000m);
        a.UpdateDetails("Laptop", AssetCategory.Electronics, null, null, null, null, Jan1, new Money(1000m, "USD"), null);
        a.SetValuationModel(ValuationMethod.StraightLine, 0m, 100m, 5);
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(1)).Amount.Should().BeApproximately(820m, 2m);
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(10)).Amount.Should().Be(100m);
    }

    [Fact]
    public void Estimate_uses_latest_valuation_as_anchor()
    {
        var a = NewAsset();
        a.SetValuationModel(ValuationMethod.DecliningBalance, 0.5m, null, null);
        a.RecordValuation(new Money(400m, "USD"), Jan1.AddYears(1), "Manual");
        AssetValuationEstimator.Estimate(a, Jan1.AddYears(2)).Amount.Should().BeApproximately(200m, 2m);
    }
}
