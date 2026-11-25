using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Application.Features.Query;

namespace FlowLedger.Api.Endpoints;

internal static class DashboardEndpoints
{
    internal sealed record CategorySpend(string Category, decimal Amount);

    internal sealed record MonthCashFlow(string Month, decimal Income, decimal Spending);

    internal sealed record NetWorthPoint(string Date, decimal Value);

    internal sealed record MerchantSpend(string Merchant, decimal Amount, int Visits);

    /// <summary>A category whose spending changed most against the previous period of the same length.</summary>
    internal sealed record CategoryMover(string Category, decimal Current, decimal Previous, decimal Change);

    internal sealed record DashboardInsights(
        IReadOnlyList<CategorySpend> SpendingByCategory,
        IReadOnlyList<MonthCashFlow> CashFlow,
        IReadOnlyList<NetWorthPoint> NetWorth,
        IReadOnlyList<MerchantSpend>? TopMerchants = null,
        IReadOnlyList<CategoryMover>? Movers = null,
        string? PreviousLabel = null);

    internal sealed record DismissSuspiciousRequest(string? Note);

    internal static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard").RequireRateLimiting("api");

        group.MapGet("/activity", async (GetDashboardActivityHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(ct)))
            .WithName("GetDashboardActivity")
            .WithSummary("Recent, pending, large and suspicious transactions across all accounts");

        group.MapPost("/suspicious/{transactionId:guid}/dismiss",
                async (Guid transactionId, DismissSuspiciousRequest? request, DismissSuspiciousHandler handler, CancellationToken ct) =>
                {
                    try
                    {
                        await handler.HandleAsync(transactionId, request?.Note, ct);
                        return Results.NoContent();
                    }
                    catch (ArgumentException ex)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = [ex.Message] });
                    }
                })
            .WithName("DismissSuspiciousTransaction")
            .WithSummary("Dismiss a flagged transaction from Suspicious Activity, with an optional note")
            .RequireRateLimiting("write");

        group.MapGet("/insights", async (RunQueryHandler query, DateOnly? from, DateOnly? to, CancellationToken ct) =>
            {
                // Optional window (default: this month for spending, last 6 months for cash flow, all history for net worth).
                var fromS = from?.ToString("yyyy-MM-dd");
                var toS = to?.ToString("yyyy-MM-dd");
                var toClause = toS is null ? "" : $" and date <= \"{toS}\"";
                // Transfers between the user's own accounts are never spending or income.
                var spending = await query.HandleAsync(
                    fromS is null
                        ? MonthlySpend.ByCategoryQuery + " | top 8 by total"
                        : $"transactions | where direction == \"Debit\" and is_transfer == false and date >= \"{fromS}\"{toClause} | summarize total = sum(amount) by category | top 8 by total", ct);
                var cashFlow = await query.HandleAsync(
                    "transactions | where date >= " + (from is { } fd ? $"\"{new DateOnly(fd.Year, fd.Month, 1):yyyy-MM-dd}\"" : "startofmonth(ago(5mo))") + toClause + " and is_transfer == false " +
                    "| summarize income = sumif(amount, direction == \"Credit\"), spending = sumif(amount, direction == \"Debit\") by month = startofmonth(date)", ct);
                var netWorth = await query.HandleAsync(
                    fromS is null && toS is null
                        ? "balance_history | summarize total = sum(balance) by date"
                        : $"balance_history | where date >= \"{fromS ?? "0001-01-01"}\"{toClause} | summarize total = sum(balance) by date", ct);

                var today = DateOnly.FromDateTime(DateTime.Now);
                // Merchants follow the selected window (this month when none).
                var curFrom = from ?? new DateOnly(today.Year, today.Month, 1);
                var curTo = to ?? today;

                // "What changed" is always the last 30 days against the 30 before, whatever the chart window is: a fixed,
                // recent comparison that stays meaningful even when history is shorter than the selected range.
                var moveTo = today;
                var moveFrom = moveTo.AddDays(-29);
                var prevTo = moveFrom.AddDays(-1);
                var prevFrom = prevTo.AddDays(-29);

                async Task<Dictionary<string, decimal>> SpendBetween(DateOnly a, DateOnly b)
                {
                    var r = await query.HandleAsync(
                        $"transactions | where direction == \"Debit\" and is_transfer == false and date >= \"{a:yyyy-MM-dd}\" and date <= \"{b:yyyy-MM-dd}\" | summarize total = sum(amount) by category", ct);
                    return r.Rows.GroupBy(x => Str(x[0]), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(x => Dec(x[1])), StringComparer.OrdinalIgnoreCase);
                }

                var cur = await SpendBetween(moveFrom, moveTo);
                var prev = await SpendBetween(prevFrom, prevTo);
                var movers = cur.Keys.Union(prev.Keys, StringComparer.OrdinalIgnoreCase)
                    .Select(k => new CategoryMover(k, cur.GetValueOrDefault(k), prev.GetValueOrDefault(k), cur.GetValueOrDefault(k) - prev.GetValueOrDefault(k)))
                    .Where(m => Math.Max(m.Current, m.Previous) >= 25m)
                    .OrderByDescending(m => Math.Abs(m.Change))
                    .Take(6).ToList();

                var merchants = await query.HandleAsync(
                    $"transactions | where direction == \"Debit\" and is_transfer == false and date >= \"{curFrom:yyyy-MM-dd}\" and date <= \"{curTo:yyyy-MM-dd}\" | summarize total = sum(amount), visits = count() by merchant | top 8 by total", ct);

                return Results.Ok(new DashboardInsights(
                    spending.Rows.Select(r => new CategorySpend(Str(r[0]), Dec(r[1]))).ToList(),
                    cashFlow.Rows.Select(r => new MonthCashFlow(Str(r[0]), Dec(r[1]), Dec(r[2]))).OrderBy(m => m.Month, StringComparer.Ordinal).ToList(),
                    netWorth.Rows.Select(r => new NetWorthPoint(Str(r[0]), Dec(r[1]))).OrderBy(p => p.Date, StringComparer.Ordinal).ToList(),
                    merchants.Rows.Select(r => new MerchantSpend(string.IsNullOrWhiteSpace(Str(r[0])) ? "Unknown" : Str(r[0]), Dec(r[1]), (int)Dec(r[2]))).ToList(),
                    movers,
                    $"{prevFrom:MMM d} – {prevTo:MMM d}"));
            })
            .WithName("GetDashboardInsights")
            .WithSummary("Spending by category (this month), 6-month income vs spending, and net-worth trend from balance snapshots");

        return app;
    }

    private static string Str(object? v) => v switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd"),
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
