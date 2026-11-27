using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.BillSheet;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.RecurringFlows;

/// <summary>Per-tenant memory of suggestions the user dismissed, so they are never offered (or auto-added) again.</summary>
public interface IRecurringDismissalStore
{
    Task<IReadOnlySet<string>> ListAsync(CancellationToken ct = default);
    Task AddAsync(string key, CancellationToken ct = default);
}

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
    CreateRecurringFlowHandler creator,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public const double AutoAddConfidence = 0.9;
    private const int ScanLimit = TransactionQuota.ProcessingCap;

    public async Task<IReadOnlyList<RecurringSuggestionDto>> GetSuggestionsAsync(CancellationToken ct = default)
    {
        var acctList = await accounts.ListAsync(ct);
        var byId = acctList.ToDictionary(a => a.Id);
        var all = await transactions.ListAllAsync(ScanLimit, ct);

        var excludedAccounts = await AccountVisibility.ExcludedIdsAsync(accounts, null, ct);
        var real = all.Where(t => !excludedAccounts.Contains(t.AccountId.Value)).Where(t => t.Status is TransactionStatus.Posted or TransactionStatus.Matched or TransactionStatus.Reconciled).ToList();

        // Recurring transfers (a monthly move to savings, a standing card payment) are kept on purpose: the money really
        // leaves one account and lands in another, so each account's forecast needs its own leg. Only the investment
        // categories below are skipped.
        // Discretionary spending (restaurants, groceries, fuel, shopping) is habit, not a bill: a weekly taco run is
        // not a recurring flow worth forecasting or alerting on.
        var categoryPaths = (await categories.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Path.Value);

        var inputs = new List<FlowTxn>();
        foreach (var t in real)
        {
            var category = t.CategoryId is { } c ? categoryPaths.GetValueOrDefault(c.Value) : null;
            if (Investments.InvestmentExclusion.IsInvestmentCategory(category))
            {
                continue;
            }

            if (!byId.TryGetValue(t.AccountId.Value, out var account) || !Eligible(account.AccountType, t.Direction))
            {
                continue;
            }

            var text = string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName;
            inputs.Add(new FlowTxn(account.Id, t.EffectiveDate, t.Amount.Amount, t.Direction == TransactionDirection.Credit, text,
                Discretionary: category is not null && IsDiscretionaryCategory(category)));
        }

        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var detected = RecurringFlowDetector.Detect(inputs, today);
        var dismissed = await dismissals.ListAsync(ct);
        var existing = (await flows.ListAsync(ct)).Where(f => f.IsActive).ToList();

        return detected
            .Where(d => !IsDismissed(d, dismissed) && !Covered(d, existing))
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

    /// <summary>
    /// "This is not really recurring": deactivates the flow and remembers the merchant so detection never adds it back
    /// (a gas station or restaurant that happened to look regular). Returns false when the flow does not exist.
    /// </summary>
    public async Task<bool> RemoveAndSuppressAsync(Guid flowId, CancellationToken ct = default)
    {
        var flow = await flows.GetByIdAsync(RecurringFlowId.From(flowId), ct);
        if (flow is null)
        {
            return false;
        }

        var isExtra = flow.Name.EndsWith(RecurringFlowDetector.ExtraSuffix, StringComparison.Ordinal);
        var basis = (isExtra ? flow.Name.Replace(RecurringFlowDetector.ExtraSuffix, string.Empty, StringComparison.Ordinal) : null)
            ?? flow.Counterparty ?? flow.Name;
        var merchant = MerchantNormalizer.Key(string.IsNullOrWhiteSpace(basis) ? flow.Name : basis);
        if (merchant.Length >= 3)
        {
            var direction = flow.Direction == TransactionDirection.Credit ? "C" : "D";
            await dismissals.AddAsync($"{flow.AccountId.Value:N}|{direction}|{merchant}{(isExtra ? "|extra" : string.Empty)}", ct);
        }

        flow.Deactivate();
        await flows.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// A dismissal matches the exact key, or the same account+direction with an overlapping merchant: keys saved before
    /// merchant normalization carried phone numbers and cities, so equality alone would let a dismissed pattern return.
    /// </summary>
    public static bool IsDismissed(DetectedFlow d, IReadOnlySet<string> dismissed)
    {
        if (dismissed.Contains(d.Key))
        {
            return true;
        }

        var parts = d.Key.Split('|');
        if (parts.Length < 3)
        {
            return false;
        }

        var merchant = parts[2];
        foreach (var raw in dismissed)
        {
            var p = raw.Split('|');
            if (p.Length < 3 || p[0] != parts[0] || p[1] != parts[1])
            {
                continue;
            }

            var dismissedExtra = p.Length > 3 && p[3] == "extra";
            if (dismissedExtra && !d.IsPeriodicExtra)
            {
                continue;
            }

            var other = p[2];
            if (other.Length >= 5 && merchant.Length >= 5 && (other.Contains(merchant) || merchant.Contains(other)))
            {
                return true;
            }
        }

        return false;
    }

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

    // Real category trees use both "Food/Dining" and "Food & Dining/Dining", "Shopping/Clothing" and "Transport/Fuel" or
    // "Auto & Transport/Gas, Fuel, & Charging", so match on the top-level group rather than one exact spelling.
    private static bool IsDiscretionaryCategory(string path) =>
        path.StartsWith("Shopping", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("Food", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Fuel", StringComparison.OrdinalIgnoreCase);

    // Fixed bills are safe to add unasked. Income is added too even when the amount varies: forecasting bills without the
    // paychecks that cover them produces false overdraft alarms, so a regular, high-confidence paycheck must be modelled.
    private static bool WouldAutoAdd(DetectedFlow d) =>
        d.Confidence >= AutoAddConfidence && (d.AmountModel == "Fixed" || d.Direction == nameof(TransactionDirection.Credit));

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
        // A periodic extra (monthly bonus) is covered only by another extra flow, and never makes its base flow look covered.
        var merchant = RecurringDetector.Key(d.Name.Replace(RecurringFlowDetector.ExtraSuffix, string.Empty, StringComparison.Ordinal));
        var direction = d.Direction == nameof(TransactionDirection.Credit) ? TransactionDirection.Credit : TransactionDirection.Debit;
        // Same account, direction, cadence and (near) amount is the same bill even when it was named differently
        // (the old flow carries the raw bank text, the new detection carries the alias).
        if (!d.IsPeriodicExtra && existing.Any(f => f.AccountId.Value == d.AccountId
                && f.Direction == direction
                && f.Pattern.Frequency.ToString() == d.Frequency
                && !f.Name.EndsWith(RecurringFlowDetector.ExtraSuffix, StringComparison.Ordinal)
                && Math.Abs(f.Amount.Amount - d.Amount) <= Math.Max(0.5m, d.Amount * 0.03m)
                && (d.Frequency == "Monthly"
                    ? f.Pattern.DayOfMonth is { } day && d.DayOfMonth is { } dd && Math.Abs(day - dd) <= 1
                    : f.Pattern.AnchorDayOfWeek?.ToString() == d.AnchorDayOfWeek
                        && (f.Pattern.IntervalWeeks ?? 0) == (d.IntervalWeeks ?? 0))))
        {
            return true;
        }

        // Standing transfers all read "Transfer to Checking ..." so a name overlap would let one flow hide every other
        // destination and amount. They are covered only by a flow for the same account suffix and (near) the same amount.
        if (RecurringFlowDetector.IsTransferText(d.Name))
        {
            var reference = RecurringFlowDetector.TransferRef(d.Name);
            return existing.Any(f => f.AccountId.Value == d.AccountId
                && f.Direction == direction
                && Math.Abs(f.Amount.Amount - d.Amount) <= Math.Max(0.5m, d.Amount * 0.03m)
                && new[] { f.Name, f.Counterparty }.Any(x => RecurringFlowDetector.IsTransferText(x)
                    && RecurringFlowDetector.TransferRef(x) == reference));
        }

        return existing.Any(f => f.AccountId.Value == d.AccountId
            && f.Name.EndsWith(RecurringFlowDetector.ExtraSuffix, StringComparison.Ordinal) == d.IsPeriodicExtra
            && new[] { f.Name, f.Counterparty }.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x =>
            {
                var k = RecurringDetector.Key(x!);
                return k.Length >= 4 && merchant.Length >= 4 && (k.Contains(merchant) || merchant.Contains(k));
            }));
    }
}
