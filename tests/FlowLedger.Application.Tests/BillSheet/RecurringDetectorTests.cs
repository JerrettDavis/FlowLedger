using FlowLedger.Application.Features.BillSheet;
using FluentAssertions;

namespace FlowLedger.Application.Tests.BillSheet;

public sealed class RecurringDetectorTests
{
    private static SheetTxn T(string label, int year, int month, int day, decimal amount)
        => new(Guid.NewGuid(), new DateOnly(year, month, day), amount, label, label);

    [Fact]
    public void Detects_steady_subscription_with_median_amount_and_day()
    {
        var r = RecurringDetector.Detect([
            T("StreamFlix", 2026, 9, 13, 15.99m), T("StreamFlix", 2026, 10, 13, 15.99m), T("StreamFlix", 2026, 11, 14, 15.99m)]);

        r.Should().ContainSingle().Which.Should().Match<DetectedBill>(b => b.DueDay == 13 && b.Estimated == 15.99m && b.Name == "StreamFlix");
    }

    [Fact]
    public void Duplicate_pending_rows_and_state_variants_do_not_hide_a_monthly_charge()
    {
        // Real shape (Super Klean): a provider re-keys the same charge, and the pending copy swaps "OK" for "USA".
        var r = RecurringDetector.Detect([
            T("SUPER KLEAN SHERIDAN TULSA OK", 2026, 8, 15, 35m),
            T("SUPER KLEAN SHERIDAN TULSA OK", 2026, 9, 18, 35m), T("SUPER KLEAN SHERIDAN TULSA OK", 2026, 9, 18, 35m),
            T("SUPER KLEAN SHERIDAN TULSA OK", 2026, 10, 15, 35m), T("SUPER KLEAN SHERIDAN TULSA OK", 2026, 10, 15, 35m),
            T("SUPER KLEAN SHERIDAN TULSA USA", 2026, 11, 15, 35m), T("SUPER KLEAN SHERIDAN TULSA USA", 2026, 11, 15, 35m),
            T("SUPER KLEAN SHERIDAN TULSA USA", 2026, 11, 15, 35m)]);

        r.Should().ContainSingle().Which.Should().Match<DetectedBill>(b => b.DueDay == 15 && b.Estimated == 35m);
    }

    [Fact]
    public void Utility_with_varying_amount_but_steady_day_is_detected()
    {
        var r = RecurringDetector.Detect([
            T("City Power", 2026, 9, 4, 80m), T("City Power", 2026, 10, 4, 210m), T("City Power", 2026, 11, 5, 140m)]);

        r.Should().ContainSingle().Which.DueDay.Should().Be(4);
    }

    [Fact]
    public void Card_reference_numbers_do_not_split_one_merchant()
    {
        var r = RecurringDetector.Detect([
            T("ATT*BILL 1234", 2026, 9, 3, 300m), T("ATT*BILL 9876", 2026, 10, 3, 300m), T("ATT*BILL 5555", 2026, 11, 3, 300m)]);

        r.Should().ContainSingle().Which.MatchText.Should().Be("ATT*BILL");
    }

    [Fact]
    public void Frequent_purchases_and_erratic_merchants_are_ignored()
    {
        var groceries = Enumerable.Range(1, 9).Select(d => T("Corner Grocery", 2026, 11, d, 40m + d)).ToList();
        var cafe = new[] { T("Clover Cafe", 2026, 9, 2, 12m), T("Clover Cafe", 2026, 10, 19, 85m), T("Clover Cafe", 2026, 11, 28, 31m) };

        RecurringDetector.Detect([.. groceries, .. cafe]).Should().BeEmpty();
    }

    [Fact]
    public void Two_months_is_not_enough()
    {
        RecurringDetector.Detect([T("Gym", 2026, 10, 9, 40m), T("Gym", 2026, 11, 9, 40m)]).Should().BeEmpty();
    }

    [Fact]
    public void Floating_day_is_null_when_day_is_inconsistent_but_amount_is_steady()
    {
        var r = RecurringDetector.Detect([T("Mowing", 2026, 9, 3, 50m), T("Mowing", 2026, 10, 20, 50m), T("Mowing", 2026, 11, 11, 50m)]);
        r.Should().ContainSingle().Which.DueDay.Should().BeNull();
    }
}
