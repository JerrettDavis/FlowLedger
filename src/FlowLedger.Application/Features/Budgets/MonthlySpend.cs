using FlowLedger.Application.Features.Query;

namespace FlowLedger.Application.Features.Budgets;

/// <summary>
/// This month's spending by category (debits only, transfers between the user's own accounts excluded), shared by
/// budgets, alerts and the dashboard.
/// </summary>
public static class MonthlySpend
{
    /// <summary>Query-language text: one row per category with <c>total</c> = sum of this month's non-transfer debits.</summary>
    public const string ByCategoryQuery =
        "transactions | where direction == \"Debit\" and is_transfer == false and date >= startofmonth(today()) " +
        "| summarize total = sum(amount) by category";

    /// <summary>Same as <see cref="ByCategoryQuery"/> but for the calendar month containing <paramref name="month"/>.</summary>
    public static string ByCategoryQueryFor(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        return "transactions | where direction == \"Debit\" and is_transfer == false " +
               $"and date >= \"{first:yyyy-MM-dd}\" and date <= \"{last:yyyy-MM-dd}\" | summarize total = sum(amount) by category";
    }

    /// <summary>Parses <see cref="ByCategoryQuery"/> rows into a case-insensitive category -> total map.</summary>
    public static Dictionary<string, decimal> Parse(QueryResult result) => result.Rows.ToDictionary(
        r => r[0]?.ToString() ?? "", r => r[1] is decimal d ? d : Convert.ToDecimal(r[1] ?? 0m), StringComparer.OrdinalIgnoreCase);

    /// <summary>Spending covered by a budget on <paramref name="category"/>: a parent ("Food") covers its children ("Food/Groceries").</summary>
    public static decimal SpentFor(string category, IReadOnlyDictionary<string, decimal> spendByCategory)
        => spendByCategory.Where(kv => kv.Key.Equals(category, StringComparison.OrdinalIgnoreCase)
                || kv.Key.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
}

/// <summary>Runs <see cref="MonthlySpend.ByCategoryQuery"/> for the current tenant.</summary>
public sealed class GetMonthlySpendByCategoryHandler(RunQueryHandler query)
{
    public async Task<Dictionary<string, decimal>> HandleAsync(CancellationToken ct = default)
        => await HandleAsync(null, ct);

    /// <summary>Spend for the given month (null = the current month).</summary>
    public async Task<Dictionary<string, decimal>> HandleAsync(DateOnly? month, CancellationToken ct = default)
        => MonthlySpend.Parse(await query.HandleAsync(
            month is { } m ? MonthlySpend.ByCategoryQueryFor(m) : MonthlySpend.ByCategoryQuery, ct));
}
