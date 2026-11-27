using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.TestSupport;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Dashboard;

public sealed class DashboardActivityAnalyzerTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);
    private static readonly Guid Acct = Guid.NewGuid();

    private static TransactionDto Tx(decimal amount, string merchant, int daysAgo, string dir = "Debit",
        string status = "Posted", Guid? account = null) =>
        new(Guid.NewGuid(), account ?? Acct, amount, "USD", dir, merchant, status, "MxAggregation",
            Today.AddDays(-daysAgo), status == "Pending" ? null : Today.AddDays(-daysAgo),
            null, merchant, null, null, TestClock.Instant);

    [Fact]
    public void Money_in_and_out_exclude_both_legs_of_a_detected_transfer_but_count_real_activity()
    {
        var checking = Guid.NewGuid();
        var savings = Guid.NewGuid();
        var paycheck = Tx(2000, "EMPLOYER", 2, "Credit", account: checking);
        var rent = Tx(900, "LANDLORD", 3, account: checking);
        var outLeg = Tx(600, "To savings", 1, "Debit", account: checking);
        var inLeg = Tx(600, "From checking", 1, "Credit", account: savings);
        var types = new Dictionary<Guid, string> { [checking] = "Checking", [savings] = "Savings" };
        var all = new List<TransactionDto> { paycheck, rent, outLeg, inLeg };

        var transferIds = TransferDetector.Detect(all, types);
        var r = DashboardActivityAnalyzer.Analyze(all, Today, transferIds: transferIds);

        transferIds.Should().BeEquivalentTo([outLeg.Id, inLeg.Id]);
        r.MoneyInLast30Days.Should().Be(2000m, "the savings leg of the transfer is not income");
        r.MoneyOutLast30Days.Should().Be(900m, "the checking leg of the transfer is not spending");
        r.TransfersExcluded.Should().Be(1);
    }

    [Fact]
    public void Money_in_and_out_count_a_lone_transfer_looking_leg_with_no_counterpart()
    {
        var r = DashboardActivityAnalyzer.Analyze([Tx(600, "To someone else", 1)], Today, transferIds: new HashSet<Guid>());

        r.MoneyOutLast30Days.Should().Be(600m);
    }

    [Fact]
    public void Dismissed_transactions_are_not_flagged()
    {
        var big = Tx(400, "NEWSHOP", 1);
        Tx(8, "TINY", 1);

        DashboardActivityAnalyzer.Analyze([big], Today).Suspicious.Should().ContainSingle();
        DashboardActivityAnalyzer.Analyze([big], Today, dismissedIds: new HashSet<Guid> { big.Id })
            .Suspicious.Should().BeEmpty();
    }

    [Fact]
    public void Separates_pending_from_recent_posted_and_ignores_planned()
    {
        var r = DashboardActivityAnalyzer.Analyze(
        [
            Tx(10, "A", 1), Tx(20, "B", 0, status: "Pending"), Tx(30, "C", 0, "Credit", "Pending"),
            Tx(99, "PLAN", 0, status: "Planned"),
        ], Today);

        r.RecentPosted.Should().ContainSingle(t => t.Description == "A");
        r.Pending.Should().HaveCount(2);
        r.PendingNetAmount.Should().Be(10m); // +30 credit - 20 debit
        r.RecentPosted.Concat(r.Pending).Should().NotContain(t => t.Status == "Planned");
    }

    [Fact]
    public void Large_transactions_use_floor_and_window()
    {
        var r = DashboardActivityAnalyzer.Analyze(
        [
            Tx(5, "SMALL", 2), Tx(1200, "RENT", 3), Tx(900, "OLD", 60),
        ], Today);

        r.LargeThreshold.Should().BeGreaterThanOrEqualTo(500m);
        r.Large.Select(t => t.Description).Should().Equal("RENT");
    }

    [Fact]
    public void Flags_duplicate_charges()
    {
        var r = DashboardActivityAnalyzer.Analyze([Tx(42.5m, "COFFEE", 1), Tx(42.5m, "COFFEE", 2)], Today);
        r.Suspicious.Should().NotBeEmpty();
        r.Suspicious.SelectMany(s => s.Reasons).Should().Contain("Possible duplicate charge");
    }

    [Fact]
    public void Flags_amount_far_above_merchant_norm()
    {
        var txs = new List<TransactionDto> { Tx(10, "GYM", 40), Tx(10, "GYM", 30), Tx(12, "GYM", 20), Tx(180, "GYM", 1) };
        var r = DashboardActivityAnalyzer.Analyze(txs, Today);
        r.Suspicious.Should().ContainSingle(s => s.Transaction.Amount == 180m)
            .Which.Reasons.Should().ContainSingle(x => x.Contains("usual amount"));
    }

    [Fact]
    public void Flags_large_charge_from_new_merchant_but_not_small_ones()
    {
        var r = DashboardActivityAnalyzer.Analyze([Tx(400, "NEWSHOP", 1), Tx(8, "TINY", 1)], Today);
        r.Suspicious.Should().ContainSingle(s => s.Transaction.Description == "NEWSHOP");
    }

    [Fact]
    public void Empty_input_yields_empty_dashboard()
    {
        var r = DashboardActivityAnalyzer.Analyze([], Today);
        r.RecentPosted.Should().BeEmpty();
        r.Suspicious.Should().BeEmpty();
        r.LargeThreshold.Should().Be(500m);
    }
}
