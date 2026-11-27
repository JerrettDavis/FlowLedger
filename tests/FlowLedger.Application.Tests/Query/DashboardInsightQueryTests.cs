using FlowLedger.Application.Features.Query;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Query;

/// <summary>The three queries behind GET /api/dashboard/insights must parse and compute on real column shapes.</summary>
public sealed class DashboardInsightQueryTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    private static QueryTable Table(string name, List<object?[]> rows)
        => new(QueryCatalog.Datasets.First(d => d.Name == name).Columns.Select(c => c.Name).ToList(), rows);

    private static object?[] Tx(string date, decimal amount, string dir, string category, bool transfer = false)
        => [DateOnly.Parse(date), amount, dir == "Debit" ? -amount : amount, dir, "d", "m", category, "Checking", "Checking", "Posted", "Manual", "USD", null, transfer];

    private static Dictionary<string, QueryTable> Tables() => new Dictionary<string, QueryTable>(StringComparer.OrdinalIgnoreCase)
    {
        ["transactions"] = Table("transactions",
        [
            Tx("2026-11-02", 100m, "Debit", "Food/Groceries"),
            Tx("2026-11-05", 50m, "Debit", "Food/Groceries"),
            Tx("2026-11-06", 30m, "Debit", "Food/Dining"),
            Tx("2026-11-03", 600m, "Debit", "Transfer", transfer: true),
            Tx("2026-11-03", 600m, "Credit", "Transfer", transfer: true),
            Tx("2026-11-01", 3000m, "Credit", "Income/Paycheck"),
            Tx("2026-10-01", 3000m, "Credit", "Income/Paycheck"),
            Tx("2026-10-12", 70m, "Debit", "Food/Groceries"),
        ]),
        ["balance_history"] = Table("balance_history",
        [
            new object?[] { DateOnly.Parse("2026-11-01"), "Checking", "Checking", 1000m },
            new object?[] { DateOnly.Parse("2026-11-01"), "Mortgage", "Mortgage", -5000m },
            new object?[] { DateOnly.Parse("2026-11-10"), "Checking", "Checking", 1500m },
            new object?[] { DateOnly.Parse("2026-11-10"), "Mortgage", "Mortgage", -4900m },
        ]),
    };

    [Fact]
    public void Spending_by_category_excludes_transfers_and_other_months()
    {
        var r = QueryEngine.Execute(
            "transactions | where direction == \"Debit\" and is_transfer == false and date >= startofmonth(today()) " +
            "| summarize total = sum(amount) by category | top 8 by total", Tables(), Today);

        r.Rows.Select(x => (string)x[0]!).Should().Equal("Food/Groceries", "Food/Dining");
        r.Rows[0][1].Should().Be(150m);
    }

    [Fact]
    public void Cash_flow_by_month_excludes_transfers()
    {
        var r = QueryEngine.Execute(
            "transactions | where date >= startofmonth(ago(5mo)) and is_transfer == false " +
            "| summarize income = sumif(amount, direction == \"Credit\"), spending = sumif(amount, direction == \"Debit\") by month = startofmonth(date)",
            Tables(), Today);

        var november = r.Rows.Single(x => x[0]!.ToString()!.StartsWith("2026-11", StringComparison.Ordinal) || x[0] is DateOnly { Month: 11 });
        november[1].Should().Be(3000m);
        november[2].Should().Be(180m);
    }

    [Fact]
    public void Net_worth_trend_sums_accounts_per_day()
    {
        var r = QueryEngine.Execute("balance_history | summarize total = sum(balance) by date", Tables(), Today);

        r.Rows.Should().HaveCount(2);
        r.Rows.Select(x => (decimal)x[1]!).Should().BeEquivalentTo([-4000m, -3400m]);
    }
}
