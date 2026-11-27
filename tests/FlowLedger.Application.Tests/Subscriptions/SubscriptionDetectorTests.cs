using FlowLedger.Application.Features.Subscriptions;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Subscriptions;

public sealed class SubscriptionDetectorTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> NoTransfers = new HashSet<Guid>();

    private static TransactionDto Debit(decimal amount, string merchant, int daysAgo) =>
        TransactionDtoBuilder.A().OnAccount(Checking).WithAmount(amount).Described(merchant).WithStatus("Posted")
            .On(Today.AddDays(-daysAgo)).Debit().Build();

    /// <summary>One charge every 30 days, newest first at <paramref name="latestDaysAgo"/>, with the given amounts oldest to newest.</summary>
    private static List<TransactionDto> Monthly(string merchant, int latestDaysAgo, params decimal[] amounts) =>
        amounts.Select((a, i) => Debit(a, merchant, latestDaysAgo + (amounts.Length - 1 - i) * 30)).ToList();

    private static IReadOnlyList<DetectedSubscription> Detect(IEnumerable<TransactionDto> history) =>
        SubscriptionDetector.Detect(history.ToList(), NoTransfers, Today);

    [Fact]
    public void A_steady_monthly_charge_is_a_subscription_with_its_monthly_cost()
    {
        var found = Detect(Monthly("Streamly Video", 4, 15.99m, 15.99m, 15.99m, 15.99m));

        var sub = found.Should().ContainSingle().Subject;
        sub.Name.Should().Be("Streamly Video");
        sub.Cadence.Should().Be("Monthly");
        sub.Status.Should().Be("Active");
        sub.Amount.Should().Be(15.99m);
        sub.MonthlyCost.Should().Be(15.99m);
        sub.Charges.Should().Be(4);
        sub.NextExpected.Should().Be(Today.AddDays(-4).AddDays(30));
        sub.PriceChange.Should().BeNull();
    }

    [Fact]
    public void Two_charges_are_not_enough_for_a_monthly_rhythm()
        => Detect(Monthly("Streamly Video", 4, 15.99m, 15.99m)).Should().BeEmpty();

    [Fact]
    public void A_recent_price_increase_is_flagged_with_the_old_and_new_amount()
    {
        var found = Detect(Monthly("Streamly Video", 4, 15.99m, 15.99m, 15.99m, 17.99m));

        var sub = found.Should().ContainSingle().Subject;
        sub.PriceChange.Should().Be("Increase");
        sub.PreviousAmount.Should().Be(15.99m);
        sub.Amount.Should().Be(17.99m);
        sub.PriceChangedOn.Should().Be(Today.AddDays(-4));
    }

    [Fact]
    public void A_price_decrease_is_flagged_too()
        => Detect(Monthly("Streamly Video", 4, 17.99m, 17.99m, 17.99m, 12.99m)).Single().PriceChange.Should().Be("Decrease");

    [Fact]
    public void An_old_price_change_is_no_longer_news()
    {
        // The rise happened five months ago and the price has been steady since.
        var found = Detect(Monthly("Streamly Video", 4, 12m, 12m, 15m, 15m, 15m, 15m, 15m, 15m));

        found.Should().ContainSingle().Which.PriceChange.Should().BeNull();
    }

    [Fact]
    public void A_one_off_blip_that_returns_to_the_usual_amount_is_not_a_price_change()
        => Detect(Monthly("Streamly Video", 4, 15.99m, 15.99m, 24.99m, 15.99m)).Single().PriceChange.Should().BeNull();

    [Fact]
    public void A_variable_bill_is_not_a_subscription()
        => Detect(Monthly("Power And Light Co", 4, 62m, 118m, 91m, 154m, 73m)).Should().BeEmpty();

    [Fact]
    public void Irregular_charges_from_one_merchant_are_not_a_subscription()
        => Detect([Debit(9m, "Corner Cafe", 3), Debit(9m, "Corner Cafe", 11), Debit(9m, "Corner Cafe", 40), Debit(9m, "Corner Cafe", 45)]).Should().BeEmpty();

    [Fact]
    public void A_yearly_charge_is_detected_with_a_monthly_equivalent_cost()
    {
        var found = Detect([Debit(120m, "Annual Cloud Plan", 20), Debit(120m, "Annual Cloud Plan", 385)]);

        var sub = found.Should().ContainSingle().Subject;
        sub.Cadence.Should().Be("Yearly");
        sub.MonthlyCost.Should().Be(10m);
    }

    [Fact]
    public void A_subscription_whose_charges_stopped_is_lapsed_and_lists_after_the_active_ones()
    {
        var history = Monthly("Streamly Video", 4, 15.99m, 15.99m, 15.99m).Concat(Monthly("Old Gym Club", 95, 40m, 40m, 40m));

        var found = Detect(history);

        found.Select(s => (s.Name, s.Status)).Should().Equal(("Streamly Video", "Active"), ("Old Gym Club", "Lapsed"));
    }

    [Fact]
    public void A_transfer_leg_is_never_a_subscription()
    {
        var charges = Monthly("Move To Savings", 4, 200m, 200m, 200m);

        SubscriptionDetector.Detect(charges, charges.Select(c => c.Id).ToHashSet(), Today).Should().BeEmpty();
    }
}
