using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Query;

namespace FlowLedger.Application.Features.Dashboard;

/// <summary>
/// Dashboard insights: spending by category, 6-month income vs spending, net-worth trend, top merchants and the biggest category
/// movers. With the SQL aggregation services wired (the normal host setup) every figure comes from database <c>GROUP BY</c> queries and
/// the bounded balance-history window, so no request loads the transaction history (#160); without them (unit tests, bare hosts) the same
/// figures are composed from the query engine over its already-scoped datasets. The two paths are pinned equal by equivalence tests.
/// </summary>
internal sealed class GetDashboardInsightsHandler(
    RunQueryHandler query,
    TimeProvider clock,
    TenantResultCache? cache = null,
    FlowLedger.SharedKernel.ITenantContext? tenant = null,
    TransactionAggregates? aggregates = null,
    IBalanceHistoryReader? balanceHistory = null,
    IAccountRepository? accountRepository = null)
{
    private bool UseSql => aggregates is not null && balanceHistory is not null && accountRepository is not null;

    /// <summary>Cached per tenant, window and day, and invalidated by any save to the tenant's data (#160); computed fresh when no cache is wired.</summary>
    public Task<DashboardInsightsDto> HandleAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        if (cache is null || tenant is null)
        {
            return ComputeAsync(from, to, ct);
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        return cache.GetOrAddAsync(tenant.TenantId, $"dashboard-insights:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{today:yyyy-MM-dd}", () => ComputeAsync(from, to, ct));
    }

    private async Task<DashboardInsightsDto> ComputeAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        // Merchants follow the selected window (this month when none).
        var curFrom = from ?? new DateOnly(today.Year, today.Month, 1);
        var curTo = to ?? today;

        // "What changed" is always the last 30 days against the 30 before, whatever the chart window is: a fixed,
        // recent comparison that stays meaningful even when history is shorter than the selected range.
        var moveTo = today;
        var moveFrom = moveTo.AddDays(-29);
        var prevTo = moveFrom.AddDays(-1);
        var prevFrom = prevTo.AddDays(-29);

        List<CategorySpendDto> spending;
        List<MonthCashFlowDto> cashFlow;
        List<NetWorthPointDto> netWorth;
        List<MerchantSpendDto> merchants;
        Dictionary<string, decimal> cur, prev;

        if (UseSql)
        {
            // The query engine reads "today" in UTC (this month, ago(5mo)); keep the SQL window on exactly the same day.
            var utcToday = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var spendRows = from is null
                ? await aggregates!.SpendByCategoryAsync(new DateOnly(utcToday.Year, utcToday.Month, 1), null, ct)
                : await aggregates!.SpendByCategoryAsync(from, to, ct);
            spending = TransactionAggregates.TopCategories(spendRows, 8).Select(r => new CategorySpendDto(r.Category, r.Total)).ToList();

            var flowFrom = from is { } fd ? new DateOnly(fd.Year, fd.Month, 1) : StartOfMonth(utcToday.AddMonths(-5));
            cashFlow = (await aggregates.CashFlowByMonthAsync(flowFrom, to, ct))
                .Select(m => new MonthCashFlowDto(m.Month, m.Income, m.Spending)).OrderBy(m => m.Month, StringComparer.Ordinal).ToList();

            var activeAccounts = (await accountRepository!.ListAsync(ct)).Select(a => a.Id).ToHashSet();
            netWorth = (await balanceHistory!.DailyAccountBalancesAsync(ct))
                .Where(p => activeAccounts.Contains(p.AccountId) && (from is null || p.Date >= from) && (to is null || p.Date <= to))
                .GroupBy(p => p.Date)
                .Select(g => new NetWorthPointDto(g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Sum(p => p.Balance)))
                .OrderBy(p => p.Date, StringComparer.Ordinal).ToList();

            cur = ToMap(await aggregates.SpendByCategoryAsync(moveFrom, moveTo, ct));
            prev = ToMap(await aggregates.SpendByCategoryAsync(prevFrom, prevTo, ct));
            merchants = TransactionAggregates.TopMerchants(await aggregates.SpendByMerchantAsync(curFrom, curTo, ct), 8)
                .Select(m => new MerchantSpendDto(string.IsNullOrWhiteSpace(m.Merchant) ? "Unknown" : m.Merchant, m.Total, m.Visits)).ToList();
        }
        else
        {
            // Optional window (default: this month for spending, last 6 months for cash flow, all history for net worth).
            var fromS = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var toS = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var toClause = toS is null ? "" : $" and date <= \"{toS}\"";
            // Transfers between the user's own accounts are never spending or income.
            var spendingResult = await query.HandleAsync(
                fromS is null
                    ? MonthlySpend.ByCategoryQuery + " | top 8 by total"
                    : $"transactions | where direction == \"Debit\" and is_transfer == false and date >= \"{fromS}\"{toClause} | summarize total = sum(amount) by category | top 8 by total", ct);
            var cashFlowResult = await query.HandleAsync(
                "transactions | where date >= " + (from is { } fd ? $"\"{new DateOnly(fd.Year, fd.Month, 1):yyyy-MM-dd}\"" : "startofmonth(ago(5mo))") + toClause + " and is_transfer == false " +
                "| summarize income = sumif(amount, direction == \"Credit\"), spending = sumif(amount, direction == \"Debit\") by month = startofmonth(date)", ct);
            var netWorthResult = await query.HandleAsync(
                fromS is null && toS is null
                    ? "balance_history | summarize total = sum(balance) by date"
                    : $"balance_history | where date >= \"{fromS ?? "0001-01-01"}\"{toClause} | summarize total = sum(balance) by date", ct);

            async Task<Dictionary<string, decimal>> SpendBetween(DateOnly a, DateOnly b)
            {
                var r = await query.HandleAsync(
                    $"transactions | where direction == \"Debit\" and is_transfer == false and date >= \"{a:yyyy-MM-dd}\" and date <= \"{b:yyyy-MM-dd}\" | summarize total = sum(amount) by category", ct);
                return r.Rows.GroupBy(x => Str(x[0]), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(x => Dec(x[1])), StringComparer.OrdinalIgnoreCase);
            }

            cur = await SpendBetween(moveFrom, moveTo);
            prev = await SpendBetween(prevFrom, prevTo);
            var merchantResult = await query.HandleAsync(
                $"transactions | where direction == \"Debit\" and is_transfer == false and date >= \"{curFrom:yyyy-MM-dd}\" and date <= \"{curTo:yyyy-MM-dd}\" | summarize total = sum(amount), visits = count() by merchant | top 8 by total", ct);

            spending = spendingResult.Rows.Select(r => new CategorySpendDto(Str(r[0]), Dec(r[1]))).ToList();
            cashFlow = cashFlowResult.Rows.Select(r => new MonthCashFlowDto(Str(r[0]), Dec(r[1]), Dec(r[2]))).OrderBy(m => m.Month, StringComparer.Ordinal).ToList();
            netWorth = netWorthResult.Rows.Select(r => new NetWorthPointDto(Str(r[0]), Dec(r[1]))).OrderBy(p => p.Date, StringComparer.Ordinal).ToList();
            merchants = merchantResult.Rows.Select(r => new MerchantSpendDto(string.IsNullOrWhiteSpace(Str(r[0])) ? "Unknown" : Str(r[0]), Dec(r[1]), (int)Dec(r[2]))).ToList();
        }

        var movers = cur.Keys.Union(prev.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k => new CategoryMoverDto(k, cur.GetValueOrDefault(k), prev.GetValueOrDefault(k), cur.GetValueOrDefault(k) - prev.GetValueOrDefault(k)))
            .Where(m => Math.Max(m.Current, m.Previous) >= 25m)
            .OrderByDescending(m => Math.Abs(m.Change))
            .Take(6).ToList();

        return new DashboardInsightsDto(spending, cashFlow, netWorth, merchants, movers, $"{prevFrom:MMM d} – {prevTo:MMM d}");
    }

    private static DateOnly StartOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private static Dictionary<string, decimal> ToMap(IReadOnlyList<CategoryTotal> rows)
        => rows.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(r => r.Total), StringComparer.OrdinalIgnoreCase);

    private static string Str(object? v) => v switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        null => "",
        _ => v.ToString() ?? "",
    };

    private static decimal Dec(object? v) => v switch
    {
        decimal d => d,
        double d => (decimal)d,
        long l => l,
        int i => i,
        _ => 0m,
    };
}
