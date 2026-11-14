namespace FlowLedger.Application.Features.Query;

/// <summary>Built-in views and reports. Read-only; users copy them via "Save as…" to customise.</summary>
public static class PresetReports
{
    public const string IdPrefix = "preset:";

    private static ReportPanel Panel(string title, string query, string kind, string? x = null, string[]? y = null, string? seriesBy = null, bool stacked = false)
        => new(title, query, new VizSpec(kind, x, y?.ToList(), seriesBy, stacked));

    private static SavedReportDto View(string slug, string name, string description, ReportPanel panel)
        => new(IdPrefix + slug, name, description, "View", true, [panel], null, null);

    private static SavedReportDto Report(string slug, string name, string description, params ReportPanel[] panels)
        => new(IdPrefix + slug, name, description, "Report", true, panels.ToList(), null, null);

    private static readonly ReportPanel SpendingByCategory = Panel("Spending by category (90 days)",
        """
        transactions
        | where direction == "Debit" and date >= ago(90d)
        | summarize total = sum(amount), transactions = count() by category
        | sort by total desc
        | take 12
        """, "pie", "category", ["total"]);

    private static readonly ReportPanel CashFlowTrend = Panel("Income vs spending (12 months)",
        """
        transactions
        | where date >= ago(12mo)
        | summarize income = sumif(amount, direction == "Credit"), spending = sumif(amount, direction == "Debit") by month = month(date)
        | extend net = income - spending
        """, "line", "month", ["income", "spending", "net"]);

    private static readonly ReportPanel CategoryTrend = Panel("Category trend (6 months)",
        """
        transactions
        | where direction == "Debit" and date >= ago(6mo)
        | summarize total = sum(amount) by month = month(date), category
        """, "line", "month", ["total"], "category");

    private static readonly ReportPanel TopMerchants = Panel("Top merchants (90 days)",
        """
        transactions
        | where direction == "Debit" and date >= ago(90d)
        | summarize total = sum(amount), visits = count() by merchant
        | top 10 by total
        """, "bar", "merchant", ["total"]);

    private static readonly ReportPanel MonthComparison = Panel("This month vs last month by category",
        """
        transactions
        | where direction == "Debit" and date >= startofmonth(ago(1mo))
        | summarize last_month = sumif(amount, date < startofmonth(today())), this_month = sumif(amount, date >= startofmonth(today())) by category
        | extend change = this_month - last_month
        | sort by this_month desc
        | take 10
        """, "column", "category", ["last_month", "this_month"]);

    private static readonly ReportPanel LargeTransactions = Panel("Large transactions (last 90 days)",
        """
        transactions
        | where amount >= 250 and date >= ago(90d)
        | project date, merchant, category, account, signed
        | sort by date desc
        | take 25
        """, "table");

    private static readonly ReportPanel Pending = Panel("Pending transactions",
        """
        transactions
        | where status == "Pending"
        | project date, merchant, account, signed
        | sort by date desc
        """, "table");

    private static readonly ReportPanel AccountBalances = Panel("Account balances",
        """
        accounts
        | where active
        | project name, type, balance
        | sort by balance desc
        """, "column", "name", ["balance"]);

    private static readonly ReportPanel AssetValues = Panel("Asset values",
        """
        assets
        | project name, category, value, gain_loss
        | sort by value desc
        """, "column", "name", ["value"]);

    private static readonly ReportPanel WeekdaySpending = Panel("Spending by day of week (90 days)",
        """
        transactions
        | where direction == "Debit" and date >= ago(90d)
        | summarize total = sum(amount), average = avg(amount) by weekday = dayofweek(date)
        """, "column", "weekday", ["total"]);

    public static IReadOnlyList<SavedReportDto> All { get; } =
    [
        View("spending-by-category", "Spending by category", "Where the last 90 days went.", SpendingByCategory),
        View("income-vs-spending", "Income vs spending trend", "Monthly income, spending and net for a year.", CashFlowTrend),
        View("category-trend", "Category trend", "Multi-line trend of spending per category.", CategoryTrend),
        View("top-merchants", "Top merchants", "Biggest merchants over 90 days.", TopMerchants),
        View("month-comparison", "Month vs last month", "Compare spending per category with the previous month.", MonthComparison),
        View("large-transactions", "Large transactions", "Everything over $250 recently.", LargeTransactions),
        View("pending", "Pending transactions", "Not yet posted by the bank.", Pending),
        View("account-balances", "Account balances", "Current balance per account.", AccountBalances),
        View("asset-values", "Asset values", "Estimated value per asset.", AssetValues),
        Report("monthly-overview", "Monthly overview",
            "Cash flow, category split, merchants and a month-over-month comparison.",
            CashFlowTrend, SpendingByCategory, MonthComparison, TopMerchants),
        Report("spending-deep-dive", "Spending deep dive",
            "Trends, merchants, weekday habits and the biggest charges.",
            CategoryTrend, TopMerchants, WeekdaySpending, LargeTransactions),
        Report("net-worth-snapshot", "Net worth snapshot",
            "Where your balances and assets stand today.",
            AccountBalances, AssetValues),
    ];

    public static SavedReportDto? Find(string id)
        => All.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
