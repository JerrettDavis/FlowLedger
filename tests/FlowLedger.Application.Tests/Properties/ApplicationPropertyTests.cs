using System.Globalization;
using CsCheck;
using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Application.Features.Insights;
using FlowLedger.TestSupport;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Properties;

/// <summary>
/// Property-based tests (CsCheck, 1000 cases each, fixed starting seed) for transfer detection, import
/// normalisation and the forecast engine. See <see cref="Prop"/> for how to replay a failing seed.
/// </summary>
public sealed class ApplicationPropertyTests
{
    // ── TransferDetector ─────────────────────────────────────────────────────

    private static readonly Guid[] Accounts = [.. Enumerable.Range(1, 4).Select(i => new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]))];

    private static readonly Dictionary<Guid, string> AccountTypes = new()
    {
        [Accounts[0]] = "Checking",
        [Accounts[1]] = "Savings",
        [Accounts[2]] = "CreditCard",
        [Accounts[3]] = "Mortgage",
    };

    private static TransactionDto Tx(Guid id, Guid account, decimal amount, string currency, string direction, int day, string status, string desc) =>
        TransactionDtoBuilder.A()
            .WithId(id).OnAccount(account).WithAmount(amount).InCurrency(currency).Described(desc).WithStatus(status).OnDay(day)
            .With(b => direction == "Credit" ? b.Credit() : b.Debit())
            .Build();

    private static readonly Gen<List<TransactionDto>> TransactionBatch =
        Gen.Select(Gen.Int[0, 3], Gen.Int[0, 3], Gen.Bool, Gen.Int[0, 12], Gen.Int[0, 9], Gen.Int[0, 9], Gen.Guid,
            (acct, amountIx, debit, day, statusIx, descIx, id) =>
                Tx(id, Accounts[acct], new[] { 10m, 25m, 25.5m, 600m }[amountIx], descIx == 0 ? "EUR" : "USD", debit ? "Debit" : "Credit", day,
                    statusIx == 0 ? "Planned" : statusIx == 1 ? "Skipped" : "Posted", descIx < 5 ? "Payment" : "Other"))
            .List[0, 30]
            .Select(list => list.DistinctBy(t => t.Id).ToList());

    [Fact]
    public void TransferDetector_only_pairs_real_legs_on_different_accounts_with_matching_amount_currency_and_window()
    {
        Prop.ForAll(TransactionBatch, batch =>
        {
            var byId = batch.ToDictionary(t => t.Id);
            var found = TransferDetector.Detect(batch, AccountTypes);

            found.All(id => byId.ContainsKey(id)).Should().BeTrue("never invents ids");
            foreach (var id in found)
            {
                var t = byId[id];
                t.Status.Should().NotBe("Planned").And.NotBe("Skipped");
                // There must be a partner leg that justifies this one (different account, same money, within the window).
                found.Any(otherId => otherId != id && byId[otherId] is var o
                        && o.AccountId != t.AccountId && o.Amount == t.Amount && o.Currency == t.Currency
                        && Math.Abs(o.EffectiveDate.DayNumber - t.EffectiveDate.DayNumber) <= TransferDetector.WindowDays)
                    .Should().BeTrue($"transaction {id} has no valid partner among the detected set");
            }
        });
    }

    [Fact]
    public void TransferDetector_pairs_are_one_to_one_so_each_amount_group_has_an_even_number_of_legs()
    {
        Prop.ForAll(TransactionBatch, batch =>
        {
            var byId = batch.ToDictionary(t => t.Id);
            var found = TransferDetector.Detect(batch, AccountTypes);
            foreach (var group in found.GroupBy(id => (byId[id].Amount, byId[id].Currency)))
            {
                (group.Count() % 2).Should().Be(0, "a transaction belongs to at most one pair");
            }
        });
    }

    [Fact]
    public void TransferDetector_with_no_liability_accounts_nets_every_pair_to_zero()
    {
        // Without a liability account only opposite-direction legs can pair, so detected debits and credits balance exactly.
        var bank = Accounts.Take(2).ToArray();
        var gen = Gen.Select(Gen.Int[0, 1], Gen.Int[0, 2], Gen.Bool, Gen.Int[0, 10], Gen.Guid,
                (acct, amountIx, debit, day, id) =>
                    Tx(id, bank[acct], new[] { 10m, 25m, 600m }[amountIx], "USD", debit ? "Debit" : "Credit", day, "Posted", "x"))
            .List[0, 30].Select(l => l.DistinctBy(t => t.Id).ToList());
        Prop.ForAll(gen, batch =>
        {
            var byId = batch.ToDictionary(t => t.Id);
            var found = TransferDetector.Detect(batch, AccountTypes);
            var net = found.Sum(id => byId[id].Direction == "Credit" ? byId[id].Amount : -byId[id].Amount);
            net.Should().Be(0m);
        });
    }

    [Fact]
    [Trait("Invariant", "INV-03")]
    public void Both_legs_of_a_detected_transfer_are_left_out_of_income_and_spending()
    {
        var accountNames = Accounts.ToDictionary(a => a, a => a.ToString());
        Prop.ForAll(TransactionBatch, batch =>
        {
            var found = TransferDetector.Detect(batch, AccountTypes);
            var from = TransactionDtoBuilder.DefaultDate.AddDays(-30);
            var to = TransactionDtoBuilder.DefaultDate.AddDays(45);
            var window = batch.Where(t => t.Status != "Planned" && t.Status != "Skipped").ToList();

            foreach (var income in new[] { false, true })
            {
                var direction = income ? "Credit" : "Debit";
                var result = SpendingInsightsAnalyzer.Analyze(
                    new BreakdownQuery(from, to, null, income), batch, new Dictionary<Guid, string>(), accountNames, AccountTypes);

                var legs = window.Where(t => t.Direction == direction && found.Contains(t.Id)).ToList();
                var rest = window.Where(t => t.Direction == direction && !found.Contains(t.Id)).Sum(t => t.Amount);
                result.Total.Should().Be(rest, "transfer legs never count toward {0}", income ? "income" : "spending");
                result.TransfersExcluded.Should().Be(legs.Count);
            }
        });
    }

    // ── ImportNormalizer ─────────────────────────────────────────────────────

    private static readonly Gen<string> Junk = Gen.String;

    private static readonly Gen<string> ImportInput = Gen.Select(
        Gen.OneOf("", "!Type:Bank\n", "OFXHEADER:100\n<OFX>\n", "Date,Description,Amount\n", "Date;Description;Amount\n", "﻿Date\tDescription\tAmount\n"),
        Junk,
        Junk,
        (prefix, a, b) => prefix + a + "\n" + b);

    [Fact]
    public void ImportNormalizer_never_throws_anything_but_InvalidOperationException_on_arbitrary_input()
    {
        Prop.ForAll(ImportInput, input =>
        {
            try
            {
                var result = ImportNormalizer.Normalize(input);
                result.RowCount.Should().BeGreaterThanOrEqualTo(0);
                result.Csv.Should().NotBeNull();
            }
            catch (InvalidOperationException)
            {
                // The documented rejection for unreadable input.
            }
        });
    }

    private static readonly Gen<string> CsvWord = Gen.Char["abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 "].Array[1, 12].Select(a => new string(a).Trim() + "x");

    private static readonly Gen<string> CanonicalCsv = Gen.Select(Gens.Date, Gens.Amount, CsvWord, CsvWord, CsvWord, (d, a, desc, merchant, cat) =>
            $"{d:yyyy-MM-dd},{a.ToString("0.00", CultureInfo.InvariantCulture)},{desc},{merchant},{cat}")
        .List[1, 25]
        .Select(rows => "Date,Amount,Description,Merchant,Category\n" + string.Join('\n', rows) + "\n");

    [Fact]
    public void ImportNormalizer_is_idempotent_on_its_own_canonical_output()
    {
        Prop.ForAll(CanonicalCsv, csv =>
        {
            var once = ImportNormalizer.Normalize(csv);
            var twice = ImportNormalizer.Normalize(once.Csv);
            twice.Csv.Should().Be(once.Csv);
            twice.RowCount.Should().Be(once.RowCount);
        });
    }

    // ── ForecastEngine ───────────────────────────────────────────────────────

    private sealed record FlowSpec(RecurrencePattern Pattern, decimal Amount, bool Credit, int StartOffset, int? EndOffset);

    private sealed record TxSpec(decimal Amount, bool Credit, int DayOffset, TransactionStatus Status);

    private sealed record ForecastCase(DateOnly AsOf, int HorizonDays, decimal Opening, List<FlowSpec> Flows, List<TxSpec> Txs);

    private static readonly TransactionStatus[] AllStatuses = Enum.GetValues<TransactionStatus>();

    private static readonly Gen<ForecastCase> ForecastCases = Gen.Select(
        Gens.Date,
        Gen.Int[0, 400],
        Gens.Amount,
        Gen.Select(Gens.Pattern, Gens.PositiveAmount, Gen.Bool, Gen.Int[-90, 90], Gen.Int[-21, 500].Select(i => i == -21 ? (int?)null : i), (p, a, c, s, e) => new FlowSpec(p, a, c, s, e)).List[0, 5],
        Gen.Select(Gens.PositiveAmount, Gen.Bool, Gen.Int[-30, 430], Gen.OneOf(AllStatuses), (a, c, d, s) => new TxSpec(a, c, d, s)).List[0, 20],
        (asOf, days, opening, flows, txs) => new ForecastCase(asOf, days, opening, flows, txs));

    private static (ForecastResult Result, decimal ExpectedClosing, AccountId Account, DateOnly End) RunForecast(ForecastCase c)
    {
        var account = AccountId.New();
        var end = c.AsOf.AddDays(c.HorizonDays);
        var request = new ForecastRequest
        {
            AsOf = c.AsOf,
            Horizon = new DateOnlyRange(c.AsOf, end),
            StartingBalances = new Dictionary<AccountId, Money> { [account] = new(c.Opening, "USD") },
            RecurringFlows = [.. c.Flows.Select(f => new ForecastFlowInput
            {
                FlowId = RecurringFlowId.New(),
                AccountId = account,
                Amount = new Money(f.Amount, "USD"),
                Direction = f.Credit ? TransactionDirection.Credit : TransactionDirection.Debit,
                Pattern = f.Pattern,
                FlowStart = c.AsOf.AddDays(f.StartOffset),
                FlowEnd = f.EndOffset is { } e ? c.AsOf.AddDays(f.StartOffset + e) : null,
            })],
            ActualTransactions = [.. c.Txs.Select(t => new ForecastTransactionInput
            {
                TransactionId = TransactionId.New(),
                AccountId = account,
                Amount = new Money(t.Amount, "USD"),
                Direction = t.Credit ? TransactionDirection.Credit : TransactionDirection.Debit,
                EffectiveDate = c.AsOf.AddDays(t.DayOffset),
                Status = t.Status,
            })],
        };

        // Naive reference: opening + every realised actual inside the horizon + every flow occurrence the day-by-day oracle finds.
        var expected = c.Opening;
        foreach (var t in c.Txs)
        {
            var date = c.AsOf.AddDays(t.DayOffset);
            var isReal = t.Status is not (TransactionStatus.Planned or TransactionStatus.Skipped or TransactionStatus.Ignored or TransactionStatus.NeedsReview);
            // #117: a non-pending actual dated on/before the as-of date is already inside the opening balance, so it is not re-applied;
            // a pending one is not yet in the balance and still applies (on day 0 when dated earlier).
            var alreadyInBalance = date <= c.AsOf && t.Status != TransactionStatus.Pending;
            if (isReal && !alreadyInBalance && date <= end)
            {
                expected += t.Credit ? t.Amount : -t.Amount;
            }
        }

        foreach (var f in c.Flows)
        {
            var start = c.AsOf.AddDays(f.StartOffset);
            DateOnly? flowEnd = f.EndOffset is { } e ? c.AsOf.AddDays(f.StartOffset + e) : null;
            var count = NaiveRecurrence.Expand(f.Pattern, start, flowEnd, c.AsOf, end).Count;
            expected += (f.Credit ? f.Amount : -f.Amount) * count;
        }

        return (new ForecastEngine().Run(request), expected, account, end);
    }

    [Fact]
    public void Forecast_closing_balance_equals_opening_plus_sum_of_flows_against_a_naive_reference()
    {
        Prop.ForAll(ForecastCases, c =>
        {
            var (result, expected, _, _) = RunForecast(c);

            var series = result.AccountSeries.Single();
            var closing = series.Points.Count == 0 ? series.StartingBalance : series.Points[^1].Balance;
            closing.Amount.Should().Be(expected);
            result.AggregateSeries.Count.Should().Be(series.Points.Select(p => p.Date).Distinct().Count());
            if (result.AggregateSeries.Count > 0)
            {
                result.AggregateSeries[^1].Balance.Should().Be(closing);
            }
        });
    }

    [Fact]
    public void Forecast_series_is_chronological_internally_consistent_and_explains_every_change()
    {
        Prop.ForAll(ForecastCases, c =>
        {
            var (result, _, _, _) = RunForecast(c);

            var series = result.AccountSeries.Single();
            var running = series.StartingBalance.Amount;
            DateOnly? previous = null;
            foreach (var point in series.Points)
            {
                if (previous is { } prev)
                {
                    point.Date.Should().BeAfter(prev);
                }

                previous = point.Date;
                point.NetChange.Amount.Should().Be(point.ContributingItems.Sum(i => i.BalanceDelta.Amount));
                running += point.NetChange.Amount;
                point.Balance.Amount.Should().Be(running);
            }

            if (series.Points.Count > 0)
            {
                result.LowWaterMarks.Single().MinBalance.Amount.Should().Be(series.Points.Min(p => p.Balance.Amount));
            }

            (result.OverdraftWarnings.Count == 1).Should().Be(series.Points.Any(p => p.Balance.IsNegative));
        });
    }
}
