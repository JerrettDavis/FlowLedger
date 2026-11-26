using FlowLedger.Application.Features.Insights;
using FlowLedger.Application.Features.Transactions;
using FluentAssertions;
using FlowLedger.TestSupport;

namespace FlowLedger.Application.Tests.Insights;

public sealed class AnomalyDetectorTests
{
    private static readonly Guid Acct = Guid.NewGuid();
    private static readonly Guid Dining = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, string> Cats = new Dictionary<Guid, string> { [Dining] = "Food/Dining" };

    private static TransactionDto Tx(string merchant, decimal amount, DateOnly date, Guid? cat = null, string dir = "Debit") =>
        new(Guid.NewGuid(), Acct, amount, "USD", dir, merchant, "Posted", "Import", date, date, cat, null, null, null, TestClock.Instant);

    private static List<TransactionDto> Monthly(string merchant, decimal amount, int count, DateOnly last) =>
        Enumerable.Range(0, count).Select(i => Tx(merchant, amount, last.AddDays(-30 * (count - i)))).ToList();

    [Fact]
    public void Flags_subscription_price_increase()
    {
        var today = new DateOnly(2026, 11, 1);
        var hist = Monthly("NETFLIX.COM", 15.49m, 5, today);
        var now = Tx("NETFLIX.COM", 17.99m, today);
        var r = AnomalyDetector.Detect([.. hist, now], [now], Cats);
        r[now.Id].Should().ContainSingle(x => x.StartsWith(AnomalyDetector.PriceIncreasePrefix));
    }

    [Fact]
    public void Same_price_is_not_flagged()
    {
        var today = new DateOnly(2026, 11, 1);
        var hist = Monthly("NETFLIX.COM", 15.49m, 5, today);
        var now = Tx("NETFLIX.COM", 15.49m, today);
        AnomalyDetector.Detect([.. hist, now], [now], Cats).Should().BeEmpty();
    }

    [Fact]
    public void Flags_merchant_outlier_scaled_to_own_variability_and_ignores_store_numbers()
    {
        var today = new DateOnly(2026, 11, 1);
        var hist = Enumerable.Range(0, 8).Select(i => Tx($"QT #{100 + i}", 40m + i, today.AddDays(-3 * (i + 1)))).ToList();
        var big = Tx("QT #999", 400m, today);
        var r = AnomalyDetector.Detect([.. hist, big], [big], Cats);
        r[big.Id].Should().ContainSingle(x => x.StartsWith(AnomalyDetector.MerchantOutlierPrefix));
    }

    [Fact]
    public void Normally_variable_merchant_is_not_flagged_for_a_typical_swing()
    {
        var today = new DateOnly(2026, 11, 1);
        var amounts = new[] { 30m, 95m, 60m, 120m, 45m, 80m };
        var hist = amounts.Select((a, i) => Tx("COSTCO", a, today.AddDays(-5 * (i + 1)))).ToList();
        var now = Tx("COSTCO", 125m, today);
        AnomalyDetector.Detect([.. hist, now], [now], Cats).Should().BeEmpty();
    }

    [Fact]
    public void Flags_category_outlier_from_a_new_merchant()
    {
        var today = new DateOnly(2026, 11, 1);
        var hist = Enumerable.Range(0, 12).Select(i => Tx($"Cafe {(char)('A' + i)}", 12m + i % 3, today.AddDays(-2 * (i + 1)), Dining)).ToList();
        var big = Tx("Brand New Steakhouse", 260m, today, Dining);
        var r = AnomalyDetector.Detect([.. hist, big], [big], Cats);
        r[big.Id].Should().ContainSingle(x => x.StartsWith(AnomalyDetector.CategoryOutlierPrefix));
    }

    [Fact]
    public void Credits_and_thin_history_are_ignored()
    {
        var today = new DateOnly(2026, 11, 1);
        var credit = Tx("ACME PAYROLL", 5000m, today, null, "Credit");
        var thin = Tx("ONE OFF", 900m, today);
        AnomalyDetector.Detect([credit, thin], [credit, thin], Cats).Should().BeEmpty();
    }
}
