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

        DateOnly horizonStart = query.HorizonStart ?? query.AsOf;
        DateOnly horizonEnd = query.HorizonEnd
            ?? query.AsOf.AddMonths(query.Months ?? 3);

        if (horizonEnd < horizonStart)
            throw new ForecastInputException("Horizon end must be on or after horizon start.");

        var horizon = new DateOnlyRange(horizonStart, horizonEnd);

        // ── Load accounts ─────────────────────────────────────────────────────

        var allAccounts = await _accounts.ListAsync(ct);
        var targetAccounts = query.AccountIds is { Count: > 0 }
            ? allAccounts.Where(a => query.AccountIds.Contains(a.AccountId.Value)).ToList()
            : allAccounts.ToList();

        if (targetAccounts.Count == 0)
            throw new ForecastInputException("No active accounts found for the forecast.");

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
                    MatchedTransactionId = o.MatchedTransactionId
                }))
            .ToList();

        // ── Load actual transactions ──────────────────────────────────────────

        // Load transactions from horizon start through horizon end; include actuals
        // that fall within the window.
        var txList = await _transactions.ListAsync(
            accountId: null,
            from: horizonStart,
            to: horizonEnd,
            skip: 0,
            take: int.MaxValue,
            ct: ct);

        var actualInputs = txList
            .Where(t => accountIds.Any(id => id == t.AccountId))
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

        return _engine.Run(forecastRequest);
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
