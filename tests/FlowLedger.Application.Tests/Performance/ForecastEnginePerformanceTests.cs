using System.Diagnostics;
using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Performance;

[Trait("Category", "Perf")]
public sealed class ForecastEnginePerformanceTests
{
    private static readonly ForecastEngine Engine = new();

    [Fact]
    public void Forecast_for_10_accounts_3_years_under_threshold()
    {
        // ── Setup: 10 accounts, 3-year horizon, monthly recurring flows ──────
        var asOf = new DateOnly(2024, 1, 1);
        var horizon = new DateOnlyRange(asOf, new DateOnly(2026, 12, 31));
        var horizonEnd = horizon.End!.Value;

        // 10 accounts with USD starting balances
        var startingBalances = new Dictionary<AccountId, Money>();
        var recurringFlows = new List<ForecastFlowInput>();

        for (int i = 0; i < 10; i++)
        {
            var accountId = new AccountId(Guid.NewGuid());
            startingBalances[accountId] = new Money(5000m, Currency.Usd);

            // Monthly recurring flow for each account (36 months = ~36 occurrences per account)
            var flow = new ForecastFlowInput
            {
                FlowId = new RecurringFlowId(Guid.NewGuid()),
                AccountId = accountId,
                Amount = new Money(100m, Currency.Usd),
                Direction = TransactionDirection.Credit,
                Pattern = RecurrencePattern.Monthly(1),
                FlowStart = asOf,
                FlowEnd = horizonEnd,
                Name = $"Monthly flow account {i}"
            };

            recurringFlows.Add(flow);
        }

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = horizon,
            StartingBalances = startingBalances,
            AccountIds = startingBalances.Keys.ToList(),
            RecurringFlows = recurringFlows,
            PlannedOccurrences = [],
            ActualTransactions = [],
            Goals = []
        };

        // ── Measure ───────────────────────────────────────────────────────────
        var stopwatch = Stopwatch.StartNew();
        var result = Engine.Run(request);
        stopwatch.Stop();

        // ── Assertions ────────────────────────────────────────────────────────
        Assert.NotNull(result);
        Assert.Equal(10, result.AccountSeries.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"Forecast took {stopwatch.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public void Csv_import_10k_rows_under_threshold()
    {
        // ── Setup: Generate a 10k-row CSV string ──────────────────────────────
        // Format: date, amount, description
        var csvBuilder = new System.Text.StringBuilder();
        csvBuilder.AppendLine("date,amount,description");

        var baseDate = new DateOnly(2024, 1, 1);
        for (int i = 0; i < 10000; i++)
        {
            var date = baseDate.AddDays(i % 365);  // Cycle through a year
            var amount = (100m + i * 0.01m).ToString("F2");
            var description = $"Transaction {i}";
            csvBuilder.AppendLine($"{date:yyyy-MM-dd},{amount},\"{description}\"");
        }

        var csvText = csvBuilder.ToString();

        // ── Measure ───────────────────────────────────────────────────────────
        var stopwatch = Stopwatch.StartNew();
        var rows = CsvParser.Parse(csvText);
        stopwatch.Stop();

        // ── Assertions ────────────────────────────────────────────────────────
        Assert.Equal(10001, rows.Count);  // 10000 data rows + 1 header row
        Assert.True(stopwatch.ElapsedMilliseconds < 3000,
            $"CSV parse took {stopwatch.ElapsedMilliseconds}ms, expected < 3000ms");

        // Verify structure: each row should have 3 fields
        foreach (var row in rows)
        {
            Assert.Equal(3, row.Count);
        }
    }
}
