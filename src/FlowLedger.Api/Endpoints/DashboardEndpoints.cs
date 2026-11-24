using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Application.Features.Query;

namespace FlowLedger.Api.Endpoints;

internal static class DashboardEndpoints
{
    internal sealed record CategorySpend(string Category, decimal Amount);

    internal sealed record MonthCashFlow(string Month, decimal Income, decimal Spending);

    internal sealed record NetWorthPoint(string Date, decimal Value);

    internal sealed record DashboardInsights(
        IReadOnlyList<CategorySpend> SpendingByCategory,
        IReadOnlyList<MonthCashFlow> CashFlow,
        IReadOnlyList<NetWorthPoint> NetWorth);

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

                return Results.Ok(new DashboardInsights(
                    spending.Rows.Select(r => new CategorySpend(Str(r[0]), Dec(r[1]))).ToList(),
                    cashFlow.Rows.Select(r => new MonthCashFlow(Str(r[0]), Dec(r[1]), Dec(r[2]))).OrderBy(m => m.Month, StringComparer.Ordinal).ToList(),
                    netWorth.Rows.Select(r => new NetWorthPoint(Str(r[0]), Dec(r[1]))).OrderBy(p => p.Date, StringComparer.Ordinal).ToList()));
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
