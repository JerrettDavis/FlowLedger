using FlowLedger.Application.Features.Strategies;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Strategies;

public class StrategyEngineTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);
    private static readonly MarketRates Rates = MarketRates.Defaults(Today);

    private static AccountSnapshot Acct(string name, string type, decimal bal, bool liability = false, bool inv = false) =>
        new(Guid.NewGuid(), name, type, bal, inv, liability);

    private static List<MonthFlow> Flows(decimal income, decimal spend, int n = 6) =>
        Enumerable.Range(1, n).Select(i => new MonthFlow($"2026-{i:00}", income, spend)).ToList();

    private static StrategyReport Run(IReadOnlyList<AccountSnapshot> accts, IReadOnlyList<MonthFlow> months,
        Dictionary<string, IReadOnlyDictionary<string, decimal>>? cats = null, MarketRates? rates = null) =>
        StrategyEngine.Analyze(new StrategyInputs(accts, months, cats ?? [], rates ?? Rates, Today), DateTimeOffset.UtcNow);

    [Fact]
    public void Large_checking_balance_yields_idle_cash_suggestion_with_impact()
    {
        var r = Run([Acct("Main", "Checking", 40_000m)], Flows(6000, 3000));
        var s = r.Suggestions.Single(x => x.Id == "idle-cash-yield");
        // (40k - 4.5k kept) * (4.00 - 0.07)% ≈ $1,395
        s.AnnualImpact.Should().BeApproximately(1395m, 5m);
        s.Assumptions.Should().NotBeEmpty();
        s.Evidence.Should().NotBeEmpty();
    }

    [Fact]
    public void Low_balance_produces_no_idle_cash_suggestion()
    {
        Run([Acct("Main", "Checking", 4_000m)], Flows(6000, 3000)).Suggestions.Should().NotContain(x => x.Id == "idle-cash-yield");
    }

    [Fact]
    public void Thin_cash_triggers_emergency_fund_warning()
    {
        var r = Run([Acct("Main", "Checking", 2_000m)], Flows(5000, 4000));
        r.Suggestions.Should().Contain(x => x.Id == "emergency-fund-low");
        r.EmergencyMonths.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Overspending_is_flagged_urgent_and_ranked_first()
    {
        var r = Run([Acct("Main", "Checking", 20_000m)], Flows(3000, 3500));
        r.Suggestions[0].Id.Should().Be("negative-savings-rate");
        r.SavingsRate.Should().BeLessThan(0);
    }

    [Fact]
    public void Surplus_cash_with_upward_curve_suggests_cd_ladder()
    {
        var rates = Rates with { Hysa = 3.5m, Cd6Month = 4.0m, Cd1Year = 4.2m, Cd2Year = 4.4m, Cd5Year = 4.6m };
        var r = Run([Acct("Savings", "Savings", 100_000m)], Flows(6000, 3000), rates: rates);
        var s = r.Suggestions.Single(x => x.Id == "cd-ladder");
        s.AnnualImpact.Should().BeGreaterThan(100m);
        s.Steps.Should().Contain(x => x.Contains("5-year"));
    }

    [Fact]
    public void Inverted_curve_does_not_recommend_ladder()
    {
        var rates = Rates with { Hysa = 4.5m, Cd6Month = 4.0m, Cd1Year = 3.8m, Cd2Year = 3.5m, Cd5Year = 3.4m };
        var r = Run([Acct("Savings", "Savings", 100_000m)], Flows(6000, 3000), rates: rates);
        r.Suggestions.Should().NotContain(x => x.Id == "cd-ladder");
        r.Suggestions.Should().Contain(x => x.Id == "cd-ladder-flat");
    }

    [Fact]
    public void Card_debt_with_ample_cash_suggests_paying_it_off()
    {
        var r = Run([Acct("Chk", "Checking", 30_000m), Acct("Visa", "CreditCard", 5_000m, liability: true)], Flows(6000, 3000));
        r.Suggestions.Should().Contain(x => x.Id == "pay-off-cards-with-cash");
    }

    [Fact]
    public void Card_debt_without_cash_suggests_consolidation()
    {
        var r = Run([Acct("Chk", "Checking", 3_000m), Acct("Visa", "CreditCard", 8_000m, liability: true)], Flows(6000, 3000));
        var s = r.Suggestions.Single(x => x.Id == "consolidate-card-debt");
        s.AnnualImpact.Should().BeApproximately(8000m * (21m - 12m) / 100m, 1m);
    }

    [Fact]
    public void Mortgage_surfaces_refinance_prompt_with_market_rate()
    {
        var r = Run([Acct("Home loan", "Mortgage", 250_000m, liability: true)], Flows(6000, 3000));
        var s = r.Suggestions.Single(x => x.Id.StartsWith("mortgage-refi-check:"));
        s.Summary.Should().Contain("6.5%");
        s.Simulator.Should().Be("refinance");
    }

    [Fact]
    public void Category_spike_is_detected_against_prior_average()
    {
        var months = Flows(6000, 3000, 6);
        var dining = Enumerable.Range(1, 6).ToDictionary(i => $"2026-{i:00}", i => i == 6 ? 600m : 200m);
        var r = Run([Acct("Chk", "Checking", 5_000m)], months, new() { ["Dining"] = dining });
        var s = r.Suggestions.Single(x => x.Id == "category-spike:Dining");
        s.AnnualImpact.Should().Be(4800m);
    }

    [Fact]
    public void Investment_accounts_are_not_counted_as_cash()
    {
        var r = Run([Acct("Brokerage", "Investment", 500_000m, inv: true)], Flows(6000, 3000));
        r.LiquidCash.Should().Be(0);
    }

    [Fact]
    public void Report_always_carries_disclaimer()
    {
        Run([], []).Disclaimer.Should().Contain("not personalised");
    }
}

public class SimulatorTests
{
    [Fact]
    public void Growth_compounds_monthly()
    {
        var pts = Simulators.Grow(10_000m, 0m, 12m, 12);
        pts[^1].Balance.Should().BeApproximately(11_268.25m, 0.05m); // 1%/month for 12 months
    }

    [Fact]
    public void Higher_rate_scenario_wins()
    {
        Simulators.CompareGrowth(20_000m, 100m, 0.1m, 100m, 4.5m, 120).Difference.Should().BeGreaterThan(5_000m);
    }

    [Fact]
    public void Past_what_if_splits_saving_from_yield()
    {
        var months = Enumerable.Range(1, 12).Select(i => ($"2026-{i:00}", 100m)).ToList();
        var r = Simulators.PastWhatIf(months, 4m, 0m, 0m, 0m);
        r.FromSaving.Should().Be(1200m);
        r.FromYield.Should().BeGreaterThan(0);
        r.TotalExtra.Should().Be(r.FromSaving + r.FromYield);
    }

    [Fact]
    public void Past_what_if_idle_cash_earns_the_rate_spread()
    {
        var months = Enumerable.Range(1, 12).Select(i => ($"2026-{i:00}", 0m)).ToList();
        var r = Simulators.PastWhatIf(months, 0m, 12_000m, 0.5m, 4.5m);
        r.TotalExtra.Should().BeApproximately(480m, 1m); // 12,000 * 4pp
    }

    [Fact]
    public void Refinance_break_even_and_lifetime()
    {
        var r = Simulators.Refinance(300_000m, 7.5m, 360, 6.0m, 360, 6_000m);
        r.MonthlySavings.Should().BeInRange(255m, 270m);
        r.BreakEvenMonths.Should().BeInRange(21, 25);
        r.Worthwhile.Should().BeTrue();
    }

    [Fact]
    public void Refinance_to_higher_rate_is_not_worthwhile()
    {
        Simulators.Refinance(200_000m, 5m, 300, 6m, 360, 3_000m).Worthwhile.Should().BeFalse();
    }

    [Fact]
    public void Loan_payment_matches_known_value()
    {
        LoanMath.Payment(200_000m, 6m, 360).Should().Be(1199.10m);
        LoanMath.Payment(1200m, 0m, 12).Should().Be(100m);
    }

    [Fact]
    public void Ladder_blends_rungs_and_compares_to_savings()
    {
        var rates = MarketRates.Defaults(new DateOnly(2026, 11, 24)) with { Hysa = 3m, Cd6Month = 4m, Cd1Year = 4m, Cd2Year = 5m, Cd5Year = 5m };
        var p = LadderPlanner.Build(40_000m, rates);
        p.Rungs.Should().HaveCount(4);
        p.BlendedApy.Should().Be(4.5m);
        p.Advantage.Should().Be(600m); // 40k * (4.5% - 3%)
    }
}

public class StrategyEngineRealRateTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);
    private static readonly MarketRates Rates = MarketRates.Defaults(Today) with { Mortgage30 = 6.0m, Auto48Month = 7.0m };

    private static AccountSnapshot Acct(string name, string type, decimal bal, decimal? rate = null, int? months = null, bool liability = false) =>
        new(Guid.NewGuid(), name, type, bal, false, liability, rate, months);

    private static List<MonthFlow> Flows() => Enumerable.Range(1, 6).Select(i => new MonthFlow($"2026-{i:00}", 6000, 3000)).ToList();

    private static StrategyReport Run(params AccountSnapshot[] a) =>
        StrategyEngine.Analyze(new StrategyInputs(a, Flows(), new Dictionary<string, IReadOnlyDictionary<string, decimal>>(), Rates, Today), DateTimeOffset.UtcNow);

    [Fact]
    public void Account_already_at_market_yield_is_not_flagged()
    {
        Run(Acct("HYSA", "Savings", 50_000m, rate: 4.0m)).Suggestions.Should().NotContain(x => x.Id == "idle-cash-yield");
    }

    [Fact]
    public void Real_savings_rate_drives_the_impact()
    {
        var s = Run(Acct("Bank", "Savings", 20_000m, rate: 1.0m)).Suggestions.Single(x => x.Id == "idle-cash-yield");
        s.AnnualImpact.Should().Be(600m); // 20,000 * (4.00 - 1.00)%
        s.Confidence.Should().BeGreaterThan(0.85);
        s.Assumptions.Should().NotContain(a => a.Contains("no rate entered"));
    }

    [Fact]
    public void Assumed_rates_are_called_out()
    {
        Run(Acct("Bank", "Savings", 20_000m)).Suggestions.Single(x => x.Id == "idle-cash-yield")
            .Assumptions.Should().Contain(a => a.Contains("no rate entered"));
    }

    [Fact]
    public void High_rate_mortgage_gets_a_concrete_refinance_recommendation()
    {
        var r = Run(Acct("Home", "Mortgage", 300_000m, rate: 7.5m, months: 336, liability: true));
        var s = r.Suggestions.Single(x => x.Id.StartsWith("mortgage-refi:"));
        s.AnnualImpact.Should().BeGreaterThan(2_000m);
        s.Summary.Should().Contain("breaks even");
    }

    [Fact]
    public void Low_rate_mortgage_is_told_not_to_refinance()
    {
        var r = Run(Acct("Home", "Mortgage", 300_000m, rate: 3.0m, months: 300, liability: true));
        r.Suggestions.Should().Contain(x => x.Id.StartsWith("mortgage-refi-no:"));
        r.Suggestions.Should().NotContain(x => x.Id.StartsWith("mortgage-refi:"));
    }

    [Fact]
    public void Expensive_car_loan_is_flagged_but_a_cheap_one_is_not()
    {
        Run(Acct("Auto loan", "Loan", 20_000m, rate: 11m, months: 48, liability: true)).Suggestions.Should().Contain(x => x.Id.StartsWith("auto-refi:"));
        Run(Acct("Auto loan", "Loan", 20_000m, rate: 6m, months: 48, liability: true)).Suggestions.Should().NotContain(x => x.Id.StartsWith("auto-refi"));
    }

    [Fact]
    public void Entered_card_apr_replaces_the_market_average()
    {
        var r = Run(Acct("Chk", "Checking", 3_000m), Acct("Store card", "CreditCard", 5_000m, rate: 29.99m, liability: true));
        // 5,000 * (29.99 - 12)% = 899.5
        r.Suggestions.Single(x => x.Id == "consolidate-card-debt").AnnualImpact.Should().BeApproximately(900m, 1m);
    }
}
