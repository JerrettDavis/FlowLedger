using FlowLedger.Application.Features.Insights;
using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Insights;

public sealed class SpendingInsightsAnalyzerTests
{
    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly Guid Card = Guid.NewGuid();
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid Restaurants = Guid.NewGuid();
    private static readonly Guid Food = Guid.NewGuid();
    private static readonly Guid Utilities = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> Cats = new()
    {
        [Groceries] = "Food/Groceries", [Restaurants] = "Food/Restaurants", [Food] = "Food", [Utilities] = "Bills/Utilities",
    };
    private static readonly Dictionary<Guid, string> Names = new() { [Checking] = "Checking", [Card] = "Card" };
    private static readonly Dictionary<Guid, string> Types = new() { [Checking] = "Checking", [Card] = "CreditCard" };

    private static TransactionDto Tx(decimal amount, string dir, string date, Guid? cat, Guid? account = null,
        string? merchant = null, string[]? tags = null, string desc = "x") =>
        new(Guid.NewGuid(), account ?? Card, amount, "USD", dir, desc, "Posted", "Manual",
            DateOnly.Parse(date), DateOnly.Parse(date), cat, merchant, null, null, DateTimeOffset.UtcNow, null, tags);

    private static BreakdownResult Run(IReadOnlyList<TransactionDto> txs, BreakdownQuery q) =>
        SpendingInsightsAnalyzer.Analyze(q, txs, Cats, Names, Types);

    private static BreakdownQuery Q(string from = "2026-11-01", string to = "2026-11-30", BreakdownGroupBy by = BreakdownGroupBy.Category,
        string? parent = null, string? select = null, bool income = false, string bucket = "month") =>
        new(DateOnly.Parse(from), DateOnly.Parse(to), null, income, by, parent, select, bucket);

    [Fact]
    public void Top_level_groups_roll_up_children_and_flag_drillable()
    {
        var res = Run([Tx(60, "Debit", "2026-11-03", Groceries), Tx(40, "Debit", "2026-11-04", Restaurants), Tx(100, "Debit", "2026-11-05", Utilities)], Q());

        res.Total.Should().Be(200);
        res.Groups.Select(g => (g.Key, g.Amount, g.HasChildren)).Should().BeEquivalentTo(
            new[] { ("Food", 100m, true), ("Bills", 100m, true) });
        res.Groups.Sum(g => g.Percent).Should().Be(100m);
    }

    [Fact]
    public void Drilling_into_parent_shows_only_its_children()
    {
        var res = Run([Tx(60, "Debit", "2026-11-03", Groceries), Tx(40, "Debit", "2026-11-04", Restaurants), Tx(100, "Debit", "2026-11-05", Utilities),
            Tx(5, "Debit", "2026-11-06", Food)], Q(parent: "Food"));

        res.Total.Should().Be(105);
        res.Groups.Select(g => g.Key).Should().BeEquivalentTo("Food/Groceries", "Food/Restaurants", "Food");
        res.Groups.Single(g => g.Key == "Food").Label.Should().Be("Food (other)");
        res.Groups.Should().OnlyContain(g => !g.HasChildren);
    }

    [Fact]
    public void Transfers_between_own_accounts_and_the_other_direction_are_excluded()
    {
        var res = Run([
            Tx(500, "Debit", "2026-11-10", null, Checking, desc: "pay card"),
            Tx(500, "Credit", "2026-11-10", null, Card, desc: "payment"),
            Tx(25, "Debit", "2026-11-11", Groceries),
            Tx(900, "Credit", "2026-11-12", null, Checking),
        ], Q());

        res.Total.Should().Be(25);
        res.TransfersExcluded.Should().Be(1);
    }

    [Fact]
    public void Tag_grouping_counts_a_multi_tag_transaction_in_each_tag_and_buckets_untagged()
    {
        var res = Run([Tx(30, "Debit", "2026-11-03", Groceries, tags: ["trip", "fun"]), Tx(10, "Debit", "2026-11-04", Groceries)], Q(by: BreakdownGroupBy.Tag));

        res.Groups.Single(g => g.Key == "trip").Amount.Should().Be(30);
        res.Groups.Single(g => g.Key == "fun").Amount.Should().Be(30);
        res.Groups.Single(g => g.Key == "Untagged").Amount.Should().Be(10);
        res.Total.Should().Be(40);
    }

    [Fact]
    public void Previous_period_total_covers_the_equal_length_window_before()
    {
        var res = Run([Tx(100, "Debit", "2026-11-05", Groceries), Tx(70, "Debit", "2026-10-05", Groceries), Tx(999, "Debit", "2026-08-05", Groceries)],
            Q(from: "2026-11-01", to: "2026-11-30"));

        res.Total.Should().Be(100);
        res.PreviousTotal.Should().Be(70);
    }

    [Fact]
    public void Selected_slice_narrows_trend_and_transactions_but_not_groups()
    {
        var res = Run([Tx(60, "Debit", "2026-11-03", Groceries), Tx(100, "Debit", "2026-11-05", Utilities)], Q(select: "Bills"));

        res.Groups.Should().HaveCount(2);
        res.Transactions.Should().ContainSingle().Which.Amount.Should().Be(100);
        res.Trend.Sum(t => t.Amount).Should().Be(100);
    }

    [Fact]
    public void Income_mode_sums_credits_and_merchant_grouping_uses_merchant_names()
    {
        var res = Run([Tx(1500, "Credit", "2026-11-05", null, Checking, merchant: "Acme Payroll"), Tx(20, "Debit", "2026-11-06", Groceries, merchant: "Aldi")],
            Q(by: BreakdownGroupBy.Merchant, income: true));

        res.Groups.Should().ContainSingle().Which.Label.Should().Be("Acme Payroll");
        res.Total.Should().Be(1500);
    }

    [Fact]
    public void Trend_fills_empty_buckets_so_gaps_render_as_zero()
    {
        var res = Run([Tx(10, "Debit", "2026-09-10", Groceries), Tx(30, "Debit", "2026-11-10", Groceries)], Q(from: "2026-09-01", to: "2026-11-30"));

        res.Trend.Select(t => (t.Bucket, t.Amount)).Should().Equal(("2026-09", 10m), ("2026-10", 0m), ("2026-11", 30m));
    }

    [Fact]
    public void Transfer_and_investment_categories_are_hidden_by_default_and_included_on_request()
    {
        var transfer = Guid.NewGuid();
        var invest = Guid.NewGuid();
        Cats[transfer] = "Transfer";
        Cats[invest] = "Investments/Brokerage";
        List<TransactionDto> txs = [Tx(40, "Debit", "2026-11-03", Groceries), Tx(300, "Debit", "2026-11-04", transfer), Tx(200, "Debit", "2026-11-05", invest)];

        var hidden = Run(txs, Q());
        hidden.Total.Should().Be(40);
        hidden.NonSpendingExcluded.Should().Be(500);

        Run(txs, Q() with { IncludeNonSpending = true }).Total.Should().Be(540);
    }

    [Fact]
    public void Splits_contribute_each_part_to_its_own_category()
    {
        var parent = Tx(100, "Debit", "2026-11-03", Groceries) with
        {
            Splits = [new TransactionSplitDto(70, Groceries, null), new TransactionSplitDto(30, Utilities, null)],
        };

        var res = Run([parent], Q());

        res.Groups.Single(g => g.Key == "Food").Amount.Should().Be(70);
        res.Groups.Single(g => g.Key == "Bills").Amount.Should().Be(30);
        res.TransactionCount.Should().Be(1);
    }
}
