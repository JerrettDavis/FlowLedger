using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Insights;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Query;

/// <summary>What a database GROUP BY over the transactions is grouped by.</summary>
public enum AggregateShape
{
    /// <summary>By category id (debits and credits summed separately).</summary>
    Category,

    /// <summary>By merchant name, falling back to the description when blank.</summary>
    Merchant,

    /// <summary>By calendar month of the posted (else effective) date.</summary>
    Month,
}

/// <summary>
/// Which rows an aggregate covers. Everything here is a SMALL set computed once per request (excluded accounts, detected transfers,
/// non-spending categories), so the database filters and groups the rest instead of the application loading the history.
/// </summary>
/// <param name="From">Inclusive lower bound on the posted-else-effective date, or null.</param>
/// <param name="To">Inclusive upper bound, or null.</param>
/// <param name="ExcludedAccountIds">Disabled and hidden accounts: their rows are not counted.</param>
/// <param name="TransferIds">Transactions detected as one leg of a transfer between the user's own accounts.</param>
/// <param name="NonSpendingCategoryIds">Categories that are not income/spending (Transfer, Investments).</param>
/// <param name="DebitsOnly">True: only debits are returned (spend by category / merchant). False: both directions (cash flow).</param>
public sealed record AggregateFilter(
    DateOnly? From,
    DateOnly? To,
    IReadOnlyCollection<Guid> ExcludedAccountIds,
    IReadOnlyCollection<Guid> TransferIds,
    IReadOnlyCollection<Guid> NonSpendingCategoryIds,
    bool DebitsOnly);

/// <summary>One database group: the key columns for the shape asked for, the sums per direction, how many rows, and the newest date in the group.</summary>
public sealed record AggregateGroup(Guid? CategoryId, string? Merchant, int Year, int Month, decimal Debit, decimal Credit, long Rows, DateOnly Newest);

/// <summary>Database access for <see cref="TransactionAggregates"/> (implemented in Infrastructure with SQL <c>GROUP BY</c>).</summary>
public interface ITransactionAggregateStore
{
    /// <summary>
    /// Every real transaction with at least one possible transfer partner (same amount and currency, another account, within
    /// <see cref="TransferDetector.WindowDays"/> days), newest first, at most <paramref name="max"/>. Transactions without a possible
    /// partner can never be paired, so detection over just these rows gives exactly the result of detection over the whole history.
    /// </summary>
    Task<IReadOnlyList<Transaction>> ListTransferCandidatesAsync(int max, CancellationToken ct = default);

    /// <summary>Groups the matching real transactions (and each part of a split one) in the database.</summary>
    Task<IReadOnlyList<AggregateGroup>> GroupAsync(AggregateFilter filter, AggregateShape shape, CancellationToken ct = default);
}

/// <summary>Spend of one category.</summary>
public sealed record CategoryTotal(string Category, decimal Total);

/// <summary>Spend at one merchant.</summary>
public sealed record MerchantTotal(string Merchant, decimal Total, int Visits);

/// <summary>Income and spending in one calendar month (<see cref="Month"/> is <c>yyyy-MM-dd</c>, the first of the month).</summary>
public sealed record MonthTotal(string Month, decimal Income, decimal Spending);

/// <summary>
/// The dashboard's common aggregations (spend by category, by merchant, income vs spending by month) answered by the database with
/// <c>GROUP BY</c> instead of loading up to <see cref="TransactionQuota.ProcessingCap"/> transactions into the query engine (#160).
/// The results match the in-memory engine over the same data (pinned by equivalence tests): same row rules (real status, visible
/// accounts, transfer legs and Transfer/Investments categories excluded, splits counted per part), same case-insensitive grouping, same
/// ordering (by key, then stable by total). Two intended differences: the engine only ever sees the newest
/// <see cref="TransactionQuota.ProcessingCap"/> rows, the SQL sees all of them; and a merchant that differs only by letter case is
/// shown with the spelling of its newest row (ties broken ordinally), where the engine shows the first one it read.
/// </summary>
public sealed class TransactionAggregates(ITransactionAggregateStore store, IAccountRepository accounts, ICategoryRepository categories)
{
    private sealed record Scope(IReadOnlySet<Guid> Excluded, IReadOnlySet<Guid> Transfers, IReadOnlySet<Guid> NonSpending, IReadOnlyDictionary<Guid, string> Paths);

    private Task<Scope>? _scope;

    /// <summary>The per-request exclusion sets are computed once and shared by every aggregate of the request.</summary>
    private Task<Scope> ScopeAsync(CancellationToken ct) => _scope ??= BuildScopeAsync(ct);

    private async Task<Scope> BuildScopeAsync(CancellationToken ct)
    {
        var accountEntities = await accounts.ListAsync(ct);
        var paths = (await categories.ListAsync(ct)).Select(CategoryMapper.ToDto).ToDictionary(c => c.Id, c => c.Path);
        var candidates = await store.ListTransferCandidatesAsync(TransactionQuota.ProcessingCap, ct);
        var transfers = TransferDetector.Detect(
            candidates.Select(TransactionMapper.ToDto).ToList(), accountEntities.ToDictionary(a => a.Id, a => a.AccountType.ToString()));
        return new Scope(
            await AccountVisibility.ExcludedIdsAsync(accounts, null, ct),
            transfers,
            paths.Where(p => SpendingInsightsAnalyzer.IsNonSpending(p.Value)).Select(p => p.Key).ToHashSet(),
            paths);
    }

    private static AggregateFilter FilterOf(Scope s, DateOnly? from, DateOnly? to, bool debitsOnly)
        => new(from, to, s.Excluded, s.Transfers, s.NonSpending, debitsOnly);

    /// <summary>Non-transfer spending per category in the window, ordered by category (case-insensitive), like <c>summarize total = sum(amount) by category</c>.</summary>
    public async Task<IReadOnlyList<CategoryTotal>> SpendByCategoryAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var groups = await store.GroupAsync(FilterOf(scope, from, to, debitsOnly: true), AggregateShape.Category, ct);
        var merged = new Dictionary<string, (string Category, decimal Total)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var category = g.CategoryId is { } id && scope.Paths.TryGetValue(id, out var path) ? path : "Uncategorized";
            merged[category] = merged.TryGetValue(category, out var seen) ? (seen.Category, seen.Total + g.Debit) : (category, g.Debit);
        }

        return merged.Values.OrderBy(v => v.Category, Comparer<string>.Create((a, b) => Values.Compare(a, b)))
            .Select(v => new CategoryTotal(v.Category, v.Total)).ToList();
    }

    /// <summary>Non-transfer spending per merchant in the window, ordered by merchant (case-insensitive).</summary>
    public async Task<IReadOnlyList<MerchantTotal>> SpendByMerchantAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var groups = await store.GroupAsync(FilterOf(scope, from, to, debitsOnly: true), AggregateShape.Merchant, ct);
        var merged = new Dictionary<string, (string Merchant, DateOnly Newest, decimal Total, long Visits)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var merchant = g.Merchant ?? "";
            if (!merged.TryGetValue(merchant, out var seen))
            {
                merged[merchant] = (merchant, g.Newest, g.Debit, g.Rows);
                continue;
            }

            // The spelling shown is the newest row's (ties: ordinal), so the result does not depend on group order.
            var newer = g.Newest > seen.Newest || (g.Newest == seen.Newest && string.CompareOrdinal(merchant, seen.Merchant) < 0);
            merged[merchant] = (newer ? merchant : seen.Merchant, newer ? g.Newest : seen.Newest, seen.Total + g.Debit, seen.Visits + g.Rows);
        }

        return merged.Values.OrderBy(v => v.Merchant, Comparer<string>.Create((a, b) => Values.Compare(a, b)))
            .Select(v => new MerchantTotal(v.Merchant, v.Total, (int)v.Visits)).ToList();
    }

    /// <summary>Non-transfer income and spending per calendar month, oldest first.</summary>
    public async Task<IReadOnlyList<MonthTotal>> CashFlowByMonthAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var groups = await store.GroupAsync(FilterOf(scope, from, to, debitsOnly: false), AggregateShape.Month, ct);
        return groups.GroupBy(g => (g.Year, g.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new MonthTotal(
                new DateOnly(g.Key.Year, g.Key.Month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Sum(x => x.Credit), g.Sum(x => x.Debit)))
            .ToList();
    }

    /// <summary>The first <paramref name="count"/> categories by total, largest first; ties keep category order (the engine's stable <c>top N by total</c>).</summary>
    public static IReadOnlyList<CategoryTotal> TopCategories(IEnumerable<CategoryTotal> rows, int count)
        => rows.OrderByDescending(r => r.Total).Take(count).ToList();

    /// <summary>The first <paramref name="count"/> merchants by total, largest first; ties keep merchant order.</summary>
    public static IReadOnlyList<MerchantTotal> TopMerchants(IEnumerable<MerchantTotal> rows, int count)
        => rows.OrderByDescending(r => r.Total).Take(count).ToList();
}
