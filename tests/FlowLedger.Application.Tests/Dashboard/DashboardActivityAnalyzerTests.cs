using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Application.Features.Transactions;
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
            null, merchant, null, null, DateTimeOffset.UtcNow);

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
