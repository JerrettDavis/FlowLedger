using FlowLedger.Application.Features.Alerts;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Alerts;

public sealed class ActivityAlertRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> NoTransfers = new HashSet<Guid>();
    private static readonly IReadOnlyDictionary<Guid, string> NoCategories = new Dictionary<Guid, string>();

    private static TransactionDto Debit(decimal amount, string merchant, int daysAgo) =>
        TransactionDtoBuilder.A().OnAccount(Checking).WithAmount(amount).Described(merchant).WithStatus("Posted")
            .On(Today.AddDays(-daysAgo)).Debit().Build();

    private static IReadOnlyList<ActivityAlert> Build(IReadOnlyList<TransactionDto> history, IReadOnlyList<BalanceInput>? balances = null, IReadOnlySet<Guid>? transfers = null) =>
        ActivityAlertRules.Build(Today, history, transfers ?? NoTransfers, balances ?? [], NoCategories);

    /// <summary>Ten ordinary small purchases spread over the quarter, so a 900 charge is clearly unusual.</summary>
    private static List<TransactionDto> Everyday() =>
        Enumerable.Range(0, 30).Select(i => Debit(8 + i % 5, $"Corner Cafe {i % 3}", 5 + i * 2)).ToList();

    [Fact]
    public void A_charge_far_above_the_household_norm_raises_a_large_charge_alert_only_when_recent()
    {
        var history = Everyday();
        var big = Debit(900, "Big Appliance Store", 1);
        var oldBig = Debit(900, "Old Appliance Store", 30);
        history.AddRange([big, oldBig]);

        var alerts = Build(history).Where(a => a.Kind == ActivityAlertKinds.LargeCharge).ToList();

        alerts.Should().ContainSingle().Which.Subject.Should().Be("Big Appliance Store");
        alerts[0].DedupeKey.Should().Be($"large_charge:{big.Id}");
    }

    [Fact]
    public void A_large_transfer_leg_is_not_a_large_charge()
    {
        var history = Everyday();
        var move = Debit(900, "To savings", 1);
        history.Add(move);

        Build(history, transfers: new HashSet<Guid> { move.Id }).Should().NotContain(a => a.Kind == ActivityAlertKinds.LargeCharge);
    }

    [Fact]
    public void A_steady_monthly_charge_that_goes_up_raises_a_price_increase_alert()
    {
        var history = Everyday();
        foreach (var monthsAgo in new[] { 5, 4, 3, 2, 1 })
        {
            history.Add(Debit(15.49m, "StreamCo", monthsAgo * 30 + 1));
        }

        history.Add(Debit(17.99m, "StreamCo", 1));

        Build(history).Should().Contain(a => a.Kind == ActivityAlertKinds.PriceIncrease && a.Subject == "StreamCo");
    }

    [Fact]
    public void A_merchant_that_just_repeated_at_a_monthly_rhythm_is_a_new_subscription()
    {
        var history = Everyday();
        history.Add(Debit(9.99m, "FreshNews Digital", 31));
        history.Add(Debit(9.99m, "FreshNews Digital", 1));

        Build(history).Should().ContainSingle(a => a.Kind == ActivityAlertKinds.NewSubscription).Which.Subject.Should().Be("FreshNews Digital");
    }

    [Fact]
    public void An_old_subscription_or_a_single_charge_is_not_new()
    {
        var history = Everyday();
        history.Add(Debit(9.99m, "Established Digital", 200));
        history.Add(Debit(9.99m, "Established Digital", 170));
        history.Add(Debit(9.99m, "Established Digital", 1));
        history.Add(Debit(19.99m, "One Time Shop", 1));

        Build(history).Should().NotContain(a => a.Kind == ActivityAlertKinds.NewSubscription);
    }

    [Fact]
    public void Low_balance_flags_positive_liquid_accounts_under_the_threshold_only()
    {
        var low = new BalanceInput(Guid.NewGuid(), "Checking", 42m, true);
        var fine = new BalanceInput(Guid.NewGuid(), "Savings", 5000m, true);
        var overdrawn = new BalanceInput(Guid.NewGuid(), "Wallet", -5m, true);
        var card = new BalanceInput(Guid.NewGuid(), "Card", 30m, false);

        var alerts = Build([], [low, fine, overdrawn, card]).Where(a => a.Kind == ActivityAlertKinds.LowBalance).ToList();

        alerts.Should().ContainSingle().Which.Subject.Should().Be("Checking");
        alerts[0].DedupeKey.Should().Be($"low_balance:{low.Id}:2026-11-20");
    }

    [Fact]
    public void A_low_account_behind_an_unfinished_goal_alerts_once_naming_its_goals_even_when_not_liquid()
    {
        var liquid = new BalanceInput(Guid.NewGuid(), "Savings", 40m, true);
        var brokerage = new BalanceInput(Guid.NewGuid(), "Brokerage", 60m, false);
        var plain = new BalanceInput(Guid.NewGuid(), "Wallet", 20m, false);
        var goals = new Dictionary<Guid, IReadOnlyList<string>> { [liquid.Id] = ["Trip", "Car"], [brokerage.Id] = ["House"] };

        var alerts = ActivityAlertRules.Build(Today, [], NoTransfers, [liquid, brokerage, plain], NoCategories, goalsByAccount: goals)
            .Where(a => a.Kind == ActivityAlertKinds.LowBalance).ToList();

        alerts.Select(a => a.Subject).Should().BeEquivalentTo(["Savings", "Brokerage"], "one alert per account, none for the unlinked non-liquid wallet");
        alerts.Single(a => a.Subject == "Savings").Detail.Should().Contain("goals \"Trip\", \"Car\"");
        alerts.Single(a => a.Subject == "Brokerage").Detail.Should().Contain("goal \"House\"");
        alerts.Single(a => a.Subject == "Savings").DedupeKey.Should().Be($"low_balance:{liquid.Id}:2026-11-20");
    }

    [Theory]
    [InlineData(500, 120, true, 880)]
    [InlineData(500, 620, true, 500)]
    [InlineData(500, 120, false, 500)]
    [InlineData(500, 0, true, 1000)]
    public void Budget_rollover_adds_last_months_unspent_amount_but_never_penalises_an_overspend(
        decimal limit, decimal previousSpent, bool rollover, decimal expected)
        => BudgetRollover.EffectiveLimit(limit, previousSpent, rollover).Should().Be(expected);
}
