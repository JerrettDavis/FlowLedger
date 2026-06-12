using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;
using System.Diagnostics;

namespace FlowLedger.Application.Tests.Features.Forecasting;

/// <summary>
/// Performance smoke test: a 3-year forecast over 10 accounts with realistic flows
/// must complete in under 500 ms (PLAN.md §23).
///
/// CI tolerance is 1500 ms to avoid flakiness on slow agents.
/// The test logs actual elapsed time regardless of pass/fail.
/// </summary>
public sealed class ForecastPerformanceTests
{
    private const int CiToleranceMs = 1500;

    [Fact]
    public void ThreeYear_10Account_forecast_completes_under_ci_tolerance()
    {
        var request = BuildLargeRequest();
        var engine = new ForecastEngine();

        // Warm-up run (JIT)
        engine.Run(request);

        // Timed run
        var sw = Stopwatch.StartNew();
        var result = engine.Run(request);
        sw.Stop();

        var elapsedMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[ForecastPerf] 3-year, 10 accounts: {elapsedMs:F1} ms  (CI limit: {CiToleranceMs} ms)");

        // Sanity: result must be non-trivial
        result.AccountSeries.Should().HaveCount(10);
        result.AccountSeries.All(s => s.Points.Count > 0).Should().BeTrue("each account must have projection points");
        result.LowWaterMarks.Should().HaveCount(10);

        elapsedMs.Should().BeLessThanOrEqualTo(CiToleranceMs,
            $"3-year forecast over 10 accounts should complete well under {CiToleranceMs} ms. Actual: {elapsedMs:F1} ms");
    }

    private static ForecastRequest BuildLargeRequest()
    {
        var currency = new Currency("USD");
        var asOf = new DateOnly(2025, 1, 1);
        var horizonEnd = asOf.AddYears(3);

        var accounts = Enumerable.Range(1, 10)
            .Select(_ => AccountId.New())
            .ToList();

        var startingBalances = accounts
            .ToDictionary(a => a, _ => new Money(5000m, currency));

        // Realistic flow set per account:
        // - Biweekly payroll (credit)
        // - Monthly mortgage/rent (debit)
        // - Monthly utilities (debit)
        // - Monthly car payment (debit)
        // - Biweekly grocery (debit)
        // - Monthly subscription (debit)

        var flows = new List<ForecastFlowInput>();
        foreach (var accountId in accounts)
        {
            flows.Add(MakeFlow(accountId, 3500m, TransactionDirection.Credit,
                RecurrencePattern.EveryNWeeks(2, DayOfWeek.Friday), asOf, "Payroll"));

            flows.Add(MakeFlow(accountId, 1800m, TransactionDirection.Debit,
                RecurrencePattern.Monthly(1), asOf, "Rent"));

            flows.Add(MakeFlow(accountId, 150m, TransactionDirection.Debit,
                RecurrencePattern.Monthly(15), asOf, "Utilities"));

            flows.Add(MakeFlow(accountId, 350m, TransactionDirection.Debit,
                RecurrencePattern.Monthly(12), asOf, "Car Payment"));

            flows.Add(MakeFlow(accountId, 220m, TransactionDirection.Debit,
                RecurrencePattern.EveryNWeeks(2, DayOfWeek.Saturday), asOf, "Groceries"));

            flows.Add(MakeFlow(accountId, 25m, TransactionDirection.Debit,
                RecurrencePattern.Monthly(5), asOf, "Subscriptions"));
        }

        return new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, horizonEnd),
            StartingBalances = startingBalances,
            AccountIds = accounts,
            RecurringFlows = flows
        };
    }

    private static ForecastFlowInput MakeFlow(
        AccountId accountId, decimal amount, TransactionDirection direction,
        RecurrencePattern pattern, DateOnly start, string name) =>
        new()
        {
            FlowId = RecurringFlowId.New(),
            AccountId = accountId,
            Amount = new Money(amount, new Currency("USD")),
            Direction = direction,
            Pattern = pattern,
            FlowStart = start,
            Name = name
        };
}
