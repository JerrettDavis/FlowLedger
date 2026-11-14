using FlowLedger.Application.Features.Query;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Query;

public sealed class QueryEngineTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    private static IReadOnlyDictionary<string, QueryTable> Data()
    {
        object?[] Tx(string date, decimal amount, string dir, string cat, string merchant, string status = "Posted")
            => [DateOnly.Parse(date), amount, dir == "Debit" ? -amount : amount, dir, merchant, merchant, cat, "Checking", "Checking", status, "Manual", "USD", null];

        var cols = QueryCatalog.Datasets.First(d => d.Name == "transactions").Columns.Select(c => c.Name).ToList();
        var rows = new List<object?[]>
        {
            Tx("2026-11-02", 100m, "Debit", "Food", "Cafe"),
            Tx("2026-11-05", 50m, "Debit", "Food", "Cafe"),
            Tx("2026-10-20", 400m, "Debit", "Rent", "Landlord"),
            Tx("2026-10-01", 3000m, "Credit", "Income", "Employer"),
            Tx("2026-11-01", 3000m, "Credit", "Income", "Employer"),
            Tx("2026-11-10", 20m, "Debit", "Food", "Deli", "Pending"),
            Tx("2025-01-01", 999m, "Debit", "Old", "Ancient"),
        };
        return new Dictionary<string, QueryTable>(StringComparer.OrdinalIgnoreCase) { ["transactions"] = new(cols, rows) };
    }

    private static QueryResult Run(string q) => QueryEngine.Execute(q, Data(), Today);

    private static object? Cell(QueryResult r, int row, string col)
        => r.Rows[row][r.Columns.ToList().FindIndex(c => c.Name == col)];

    [Fact]
    public void Where_and_take_filter_rows()
    {
        var r = Run("transactions | where direction == \"Debit\" and category == \"Food\" | take 2");
        r.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void Summarize_groups_and_sorts_by_key_ascending()
    {
        var r = Run("transactions | where direction == \"Debit\" and date >= ago(1mo) | summarize total = sum(amount), n = count() by category");
        r.Columns.Select(c => c.Name).Should().Equal("category", "total", "n");
        r.Rows.Select(x => x[0]).Should().Equal("Food", "Rent");
        Cell(r, 0, "total").Should().Be(170m);
        Cell(r, 0, "n").Should().Be(3m);
    }

    [Fact]
    public void Month_function_supports_trending_with_sumif()
    {
        var r = Run("transactions | where date >= ago(2mo) and status == \"Posted\" | summarize income = sumif(amount, direction == \"Credit\"), spending = sumif(amount, direction == \"Debit\") by m = month(date)");
        r.Rows.Select(x => x[0]).Should().Equal("2026-10", "2026-11");
        Cell(r, 1, "income").Should().Be(3000m);
        Cell(r, 1, "spending").Should().Be(150m);
    }

    [Fact]
    public void Sort_top_extend_and_project_compose()
    {
        var r = Run("transactions | where direction == \"Debit\" | extend pct = amount / 10 | top 1 by amount | project merchant, pct");
        r.Rows.Should().HaveCount(1);
        Cell(r, 0, "merchant").Should().Be("Ancient");
        Cell(r, 0, "pct").Should().Be(99.9m);
    }

    [Fact]
    public void Count_distinct_and_in_operators_work()
    {
        Run("transactions | count").Rows[0][0].Should().Be(7m);
        Run("transactions | distinct category").Rows.Should().HaveCount(4);
        Run("transactions | where category in (\"Food\", \"Rent\") | count").Rows[0][0].Should().Be(4m);
        Run("transactions | where merchant contains \"caf\" | count").Rows[0][0].Should().Be(2m);
        Run("transactions | where category not in (\"Food\") | count").Rows[0][0].Should().NotBeNull();
    }

    [Fact]
    public void Aggregate_over_empty_input_returns_one_row()
    {
        var r = Run("transactions | where amount > 1000000 | summarize n = count(), s = sum(amount)");
        r.Rows.Should().HaveCount(1);
        Cell(r, 0, "n").Should().Be(0m);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("nope | take 1", "Unknown dataset")]
    [InlineData("transactions | where nope == 1", "Unknown column 'nope'")]
    [InlineData("transactions | wherr amount > 1", "Unknown operator")]
    [InlineData("transactions | summarize total = sum(amount) by", "Unexpected")]
    [InlineData("transactions | where amount >", "Unexpected")]
    [InlineData("transactions | where \"open", "Unterminated")]
    [InlineData("transactions | summarize x = sum(category)", "numeric")]
    [InlineData("transactions | where sum(amount) > 1", "aggregate")]
    [InlineData("transactions take 1", "forget a '|'")]
    [InlineData("transactions | where date > ago(99999999d)", "out of range")]
    [InlineData("transactions | extend x = amount * 99999999999999999999 * 99999999999999999999", "out of range")]
    [InlineData("transactions | where date > today() - 999999999999", "out of range")]
    public void Bad_queries_raise_readable_errors(string query, string expected)
    {
        var act = () => Run(query);
        act.Should().Throw<QueryException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Row_limit_marks_result_truncated()
    {
        var cols = new[] { "n" };
        var rows = Enumerable.Range(0, QueryEngine.MaxResultRows + 10).Select(i => new object?[] { (decimal)i }).ToList();
        var r = QueryEngine.Execute("t | where n >= 0", new Dictionary<string, QueryTable> { ["t"] = new(cols, rows) }, Today);
        r.Truncated.Should().BeTrue();
        r.Rows.Should().HaveCount(QueryEngine.MaxResultRows);
    }

    [Fact]
    public void Presets_all_parse_and_run_against_the_catalog()
    {
        var tables = QueryCatalog.Datasets.ToDictionary(
            d => d.Name, d => new QueryTable(d.Columns.Select(c => c.Name).ToList(), []), StringComparer.OrdinalIgnoreCase);

        foreach (var preset in PresetReports.All)
        {
            foreach (var panel in preset.Panels)
            {
                var act = () => QueryEngine.Execute(panel.Query, tables, Today);
                act.Should().NotThrow($"preset '{preset.Name}' / '{panel.Title}' must be valid");
            }
        }
    }

    [Fact]
    public void Presets_run_on_real_rows()
    {
        var tables = new Dictionary<string, QueryTable>(Data(), StringComparer.OrdinalIgnoreCase);
        foreach (var d in QueryCatalog.Datasets.Where(d => !tables.ContainsKey(d.Name)))
        {
            tables[d.Name] = new QueryTable(d.Columns.Select(c => c.Name).ToList(), []);
        }

        var trend = QueryEngine.Execute(PresetReports.Find("preset:income-vs-spending")!.Panels[0].Query, tables, Today);
        trend.Rows.Should().NotBeEmpty();
        var compare = QueryEngine.Execute(PresetReports.Find("preset:month-comparison")!.Panels[0].Query, tables, Today);
        compare.Rows.Should().Contain(r => (string?)r[0] == "Food");
    }
}
