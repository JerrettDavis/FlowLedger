using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.BillSheet;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.RecurringFlows;

/// <summary>Per-tenant memory of suggestions the user dismissed, so they are never offered (or auto-added) again.</summary>
public interface IRecurringDismissalStore
{
    Task<IReadOnlySet<string>> ListAsync(CancellationToken ct = default);
    Task AddAsync(string key, CancellationToken ct = default);
}

/// <summary>A detected recurring pattern, as shown to the user.</summary>
public sealed record RecurringSuggestionDto(
    string Key,
    Guid AccountId,
    string AccountName,
    string Name,
    string Direction,
    decimal Amount,
    string Currency,
    string AmountModel,
    string Frequency,
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly LastSeen,
    DateOnly NextExpected,
    int Occurrences,
    double Confidence,
    bool WouldAutoAdd);

public sealed record AutoAddResult(int Added, IReadOnlyList<string> Names);

/// <summary>
/// Turns detected recurring patterns into recurring flows (which then drive forecasts and "upcoming" lists).
/// Obvious ones (fixed amount, very regular) are added automatically after a sync; everything else is offered as a
/// suggestion the user can accept or dismiss. Patterns already covered by an existing flow, or dismissed, never reappear.
/// </summary>
public sealed class RecurringSuggestionService(
    ITransactionRepository transactions,
    IAccountRepository accounts,
    IRecurringFlowRepository flows,
    ICategoryRepository categories,
    IRecurringDismissalStore dismissals,
    CreateRecurringFlowHandler creator)
{
    public const double AutoAddConfidence = 0.9;
    private const int ScanLimit = 50_000;

    public async Task<IReadOnlyList<RecurringSuggestionDto>> GetSuggestionsAsync(CancellationToken ct = default)
    {
        var acctList = await accounts.ListAsync(ct);
        var byId = acctList.ToDictionary(a => a.Id);
        var all = await transactions.ListAllAsync(ScanLimit, ct);

        var real = all.Where(t => t.Status is TransactionStatus.Posted or TransactionStatus.Matched or TransactionStatus.Reconciled).ToList();

        // Recurring transfers (a monthly move to savings, a standing card payment) are kept on purpose: the money really
        // leaves one account and lands in another, so each account's forecast needs its own leg. Only the investment
        // categories below are skipped.
        // Discretionary spending (restaurants, groceries, fuel, shopping) is habit, not a bill: a weekly taco run is
        // not a recurring flow worth forecasting or alerting on.
        var skipCategories = (await categories.ListAsync(ct))
            .Where(c => IsIneligibleCategory(c.Path.Value))
            .Select(c => c.Id).ToHashSet();

        var inputs = new List<FlowTxn>();
        foreach (var t in real)
        {
            if ((t.CategoryId is { } c && skipCategories.Contains(c.Value)))
            {
                continue;
            }

            if (!byId.TryGetValue(t.AccountId.Value, out var account) || !Eligible(account.AccountType, t.Direction))
            {
                continue;
            }

            var text = string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName;
            inputs.Add(new FlowTxn(account.Id, t.EffectiveDate, t.Amount.Amount, t.Direction == TransactionDirection.Credit, text));
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var detected = RecurringFlowDetector.Detect(inputs, today);
        var dismissed = await dismissals.ListAsync(ct);
        var existing = (await flows.ListAsync(ct)).Where(f => f.IsActive).ToList();

        return detected
            .Where(d => !dismissed.Contains(d.Key) && !Covered(d, existing))
            .Select(d =>
            {
                var account = byId[d.AccountId];
                return new RecurringSuggestionDto(
                    d.Key, d.AccountId, account.Name, d.Name, d.Direction, d.Amount, account.CurrentBalance.Currency.Code,
                    d.AmountModel, d.Frequency, d.DayOfMonth, d.SecondDayOfMonth, d.IntervalWeeks, d.AnchorDayOfWeek,
                    d.LastSeen, d.NextExpected, d.Occurrences, d.Confidence, WouldAutoAdd(d));
            })
            .ToList();
    }

    public async Task<RecurringFlowDto?> AcceptAsync(string key, CancellationToken ct = default)
    {
        var suggestion = (await GetSuggestionsAsync(ct)).FirstOrDefault(s => s.Key == key);
        return suggestion is null ? null : await CreateAsync(suggestion, ct);
    }

    public Task DismissAsync(string key, CancellationToken ct = default) => dismissals.AddAsync(key, ct);

    /// <summary>Adds every suggestion that is safe to add without asking. Idempotent: added flows stop being suggested.</summary>
    public async Task<AutoAddResult> AutoAddAsync(CancellationToken ct = default)
    {
        var names = new List<string>();
        foreach (var s in (await GetSuggestionsAsync(ct)).Where(s => s.WouldAutoAdd))
        {
            await CreateAsync(s, ct);
            names.Add(s.Name);
        }

        return new AutoAddResult(names.Count, names);
    }

    private Task<RecurringFlowDto> CreateAsync(RecurringSuggestionDto s, CancellationToken ct) =>
        creator.HandleAsync(new CreateRecurringFlowRequest(
            s.AccountId, s.Name, s.Amount, s.Currency, s.Direction, s.AmountModel, s.Frequency,
            s.DayOfMonth, s.SecondDayOfMonth, s.IntervalWeeks, s.AnchorDayOfWeek,
            StartDate: s.NextExpected, EndDate: null, CategoryId: null, Counterparty: s.Name), ct);

    private static bool IsIneligibleCategory(string path) =>
        path == "Shopping"
        || path == "Transport/Fuel"
        || path.StartsWith("Investments/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("Food/", StringComparison.OrdinalIgnoreCase);

    // Fixed bills are safe to add unasked. Income is added too even when the amount varies: forecasting bills without the
    // paychecks that cover them produces false overdraft alarms, so a regular, high-confidence paycheck must be modelled.
    private static bool WouldAutoAdd(DetectedFlow d) =>
        d.Confidence >= AutoAddConfidence && (d.AmountModel == "Fixed" || d.Direction == "Credit");

    /// <summary>Loan/mortgage/investment accounts hold balances, not bills; card accounts only produce charges, not "income".</summary>
    private static bool Eligible(AccountType type, TransactionDirection direction) => type switch
    {
        AccountType.Checking or AccountType.Savings or AccountType.Cash => true,
        AccountType.CreditCard => direction == TransactionDirection.Debit,
        _ => false,
    };

    /// <summary>An existing active flow on the same account whose name/counterparty overlaps the merchant covers the pattern.</summary>
    private static bool Covered(DetectedFlow d, IReadOnlyList<RecurringFlow> existing)
    {
        var merchant = RecurringDetector.Key(d.Name);
        return existing.Any(f => f.AccountId.Value == d.AccountId
            && new[] { f.Name, f.Counterparty }.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x =>
            {
                var k = RecurringDetector.Key(x!);
                return k.Length >= 4 && merchant.Length >= 4 && (k.Contains(merchant) || merchant.Contains(k));
            }));
    }
}
