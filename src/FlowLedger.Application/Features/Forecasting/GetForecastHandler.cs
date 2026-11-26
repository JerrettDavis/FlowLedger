using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Forecasting;

/// <summary>
/// Application-layer query handler for running a forecast (PLAN.md §11, Milestone 4).
///
/// This handler:
/// 1. Loads all active accounts (or a filtered subset) from the repository.
/// 2. Loads all active recurring flows for the tenant.
/// 3. Loads planned occurrences and actual transactions within (and slightly before) the horizon.
/// 4. Delegates all projection logic to <see cref="IForecastEngine"/> — pure and deterministic.
/// 5. Returns a <see cref="ForecastResult"/> with full explainability.
/// </summary>
public sealed class GetForecastHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IRecurringFlowRepository _flows;
    private readonly ITransactionRepository _transactions;
    private readonly IForecastEngine _engine;
    private readonly ITenantContext _tenant;

    private const int PendingLookbackDays = 14;

    public GetForecastHandler(
        IAccountRepository accounts,
        IRecurringFlowRepository flows,
        ITransactionRepository transactions,
        IForecastEngine engine,
        ITenantContext tenant)
    {
        _accounts = accounts;
        _flows = flows;
        _transactions = transactions;
        _engine = engine;
        _tenant = tenant;
    }

    public async Task<ForecastResult> HandleAsync(
        GetForecastQuery query,
        CancellationToken ct = default)
    {
        // ── Resolve horizon ───────────────────────────────────────────────────

        // History belongs to a different view: the starting balance is as of AsOf, so never start earlier.
        DateOnly horizonStart = query.HorizonStart is { } requested && requested > query.AsOf ? requested : query.AsOf;
        DateOnly horizonEnd = query.HorizonEnd
            ?? query.AsOf.AddMonths(query.Months ?? 3);

        if (horizonEnd < horizonStart)
        {
            throw new ForecastInputException("Horizon end must be on or after horizon start.");
        }

        var horizon = new DateOnlyRange(horizonStart, horizonEnd);

        // ── Load accounts ─────────────────────────────────────────────────────

        var allAccounts = await _accounts.ListAsync(ct);
        var targetAccounts = query.AccountIds is { Count: > 0 }
            ? allAccounts.Where(a => query.AccountIds.Contains(a.AccountId.Value)).ToList()
            : LiquidOrAll(allAccounts);

        if (targetAccounts.Count == 0)
        {
            throw new ForecastInputException("No active accounts found for the forecast.");
        }

        var startingBalances = targetAccounts
            .ToDictionary(a => a.AccountId, a => a.CurrentBalance);

        var accountIds = targetAccounts.Select(a => a.AccountId).ToList();

        // ── Load recurring flows ──────────────────────────────────────────────

        var allFlows = await _flows.ListAsync(ct);
        var activeFlows = allFlows
            .Where(f => f.IsActive && accountIds.Any(id => id == f.AccountId))
            .ToList();

        var flowInputs = activeFlows.Select(f => new ForecastFlowInput
        {
            FlowId = f.RecurringFlowId,
            AccountId = f.AccountId,
            Amount = f.Amount,
            Direction = f.Direction,
            Pattern = f.Pattern,
            FlowStart = f.ActiveWindow.Start,
            FlowEnd = f.ActiveWindow.End,
            Name = f.Name
        }).ToList();

        // ── Load planned occurrences from Money Plan ──────────────────────────

        // Planned occurrences are owned by RecurringFlow aggregates
        var plannedOccurrences = activeFlows
            .SelectMany(f => f.Occurrences
                .Where(o => o.PlannedDate >= horizonStart && o.PlannedDate <= horizonEnd)
                .Select(o => new ForecastOccurrenceInput
                {
                    OccurrenceId = o.PlannedOccurrenceId,
                    FlowId = f.RecurringFlowId,
                    AccountId = o.AccountId,
                    PlannedAmount = o.PlannedAmount,
                    Direction = o.Direction,
                    PlannedDate = o.PlannedDate,
                    MatchedTransactionId = o.MatchedTransactionId,
                    FlowName = f.Name
                }))
            .ToList();

        // ── Load actual transactions ──────────────────────────────────────────

        // Load transactions from horizon start through horizon end; include actuals
        // that fall within the window.
        // A short look-back picks up pending items dated just before the horizon (not yet in the settled balance).
        var txList = await _transactions.ListForAccountsAsync(accountIds, horizonStart.AddDays(-PendingLookbackDays), horizonEnd, ct);

        var actualInputs = txList
            .Select(t => new ForecastTransactionInput
            {
                TransactionId = t.TransactionId,
                AccountId = t.AccountId,
                Amount = t.Amount,
                Direction = t.Direction,
                EffectiveDate = t.EffectiveDate,
                Status = t.Status,
                MatchedOccurrenceId = t.MatchedOccurrenceId,
                Description = t.Description
            })
            .ToList();

        // ── Build and run request ─────────────────────────────────────────────

        var forecastRequest = new ForecastRequest
        {
            AsOf = query.AsOf,
            Horizon = horizon,
            StartingBalances = startingBalances,
            AccountIds = accountIds,
            RecurringFlows = flowInputs,
            PlannedOccurrences = plannedOccurrences,
            ActualTransactions = actualInputs,
            Goals = query.Goals ?? []
        };

        var result = _engine.Run(forecastRequest);

        // Everyday spending the schedule does not cover: debits from the last ~26 weeks, minus transfers and anything an
        // active recurring flow already accounts for. Best-effort: a failure here must never break the deterministic forecast.
        try
        {
            var historyFrom = query.AsOf.AddDays(-7 * ForecastBands.HistoryWeeks);
            var history = await _transactions.ListForAccountsAsync(accountIds, historyFrom, query.AsOf, ct);
            var dtos = history.Select(Transactions.TransactionMapper.ToDto).ToList();
            var types = allAccounts.ToDictionary(a => a.Id, a => a.AccountType.ToString());
            var transfers = Transactions.TransferDetector.Detect(dtos, types);
            var flowKeys = activeFlows.Where(f => f.Direction == Domain.Aggregates.TransactionDirection.Debit)
                .SelectMany(f => new[] { f.Name, f.Counterparty })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => BillSheet.RecurringDetector.Key(x!))
                .Where(k => k.Length >= 4).Distinct().ToList();

            var debitFlows = activeFlows.Where(f => f.Direction == Domain.Aggregates.TransactionDirection.Debit).ToList();

            bool Scheduled(Transactions.TransactionDto t)
            {
                var k = BillSheet.RecurringDetector.Key(string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName);
                if (k.Length >= 4 && flowKeys.Any(f => k.Contains(f) || f.Contains(k)))
                {
                    return true;
                }

                // Bank descriptors rarely match a flow's name ("AEP" vs "AMER ELECT PWR"), so also treat a charge on the same
                // account near a monthly flow's billing day, at a similar amount, as that flow. Otherwise the bill would be
                // projected by the schedule AND counted again as everyday spending.
                var day = (t.PostedDate ?? t.EffectiveDate).Day;
                return debitFlows.Any(f => f.AccountId.Value == t.AccountId
                    && f.Pattern.DayOfMonth is { } dom
                    && Math.Abs(dom - day) <= 3
                    && f.Amount.Amount > 0
                    && Math.Abs(t.Amount - f.Amount.Amount) <= f.Amount.Amount * 0.4m);
            }

            var unscheduled = dtos
                .Where(t => t.Direction == "Debit" && t.Status is not ("Planned" or "Skipped") && !transfers.Contains(t.Id) && !Scheduled(t))
                .Select(t => (t.PostedDate ?? t.EffectiveDate, t.Amount));
            var profile = ForecastBands.BuildProfile(unscheduled, query.AsOf);
            result.SpendingProfile = profile;
            result.Bands = ForecastBands.Compute(
                result.AggregateSeries, query.AsOf, horizonEnd, startingBalances.Values.Sum(m => m.Amount), profile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the deterministic forecast; bands stay empty.
        }

        return result;
    }

    /// <summary>
    /// With no explicit account filter the forecast covers spendable cash (checking, savings, cash). Credit cards,
    /// loans/mortgages and investments would otherwise drag the aggregate far below zero and raise false overdraft
    /// alarms. Falls back to every account when the tenant has no liquid account.
    /// </summary>
    private static List<Domain.Aggregates.Account> LiquidOrAll(IReadOnlyList<Domain.Aggregates.Account> all)
    {
        var liquid = all.Where(a => a.AccountType is
            Domain.Aggregates.AccountType.Checking or
            Domain.Aggregates.AccountType.Savings or
            Domain.Aggregates.AccountType.Cash).ToList();
        return liquid.Count > 0 ? liquid : all.ToList();
    }
}

/// <summary>
/// Query parameters for <see cref="GetForecastHandler"/>.
/// </summary>
public sealed class GetForecastQuery
{
    /// <summary>
    /// As-of date for the forecast. Defaults to today in the API layer; passed explicitly
    /// for determinism in tests.
    /// </summary>
    public required DateOnly AsOf { get; init; }

    /// <summary>Optional explicit horizon start (defaults to AsOf).</summary>
    public DateOnly? HorizonStart { get; init; }

    /// <summary>Optional explicit horizon end (takes precedence over Months).</summary>
    public DateOnly? HorizonEnd { get; init; }

    /// <summary>Horizon in months from AsOf when HorizonEnd is not specified (default 3).</summary>
    public int? Months { get; init; }

    /// <summary>Filter to specific account IDs; if null/empty, all accounts are included.</summary>
    public IReadOnlyList<Guid>? AccountIds { get; init; }

    /// <summary>Optional goals to evaluate for affordability.</summary>
    public IReadOnlyList<GoalAffordabilityInput>? Goals { get; init; }
}
