using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Insights;

/// <summary>Which categories a read is limited to. <see cref="IncludeUncategorized"/> also admits rows without a category.</summary>
public sealed record BreakdownCategoryScope(IReadOnlyCollection<Guid> Ids, bool IncludeUncategorized);

/// <summary>
/// Which rows a breakdown read covers. Every collection is a small per-request set (excluded accounts, detected transfers, category ids),
/// so the database filters and groups the rest. <see cref="From"/>/<see cref="To"/> bound the posted-else-effective date, inclusive.
/// </summary>
/// <param name="Credit">True for income (credits), false for spending (debits).</param>
/// <param name="AccountIds">Only these accounts; empty means all.</param>
/// <param name="ExcludedAccountIds">Disabled and hidden accounts.</param>
/// <param name="TransferIds">Transactions that are one leg of a transfer: left out (or, for the transfer count, the only ones counted).</param>
/// <param name="HiddenCategoryIds">Non-spending categories to drop.</param>
/// <param name="Categories">Restrict to these categories (drill-down parent and selected slice), or null.</param>
/// <param name="Tag">Restrict to this tag (case-insensitive).</param>
/// <param name="Untagged">Restrict to transactions without a tag.</param>
/// <param name="Merchant">Restrict to this merchant name (case-insensitive).</param>
/// <param name="UnknownMerchant">Restrict to transactions without a merchant name (blank counts as none).</param>
/// <param name="AccountId">Restrict to one account.</param>
/// <param name="MatchNothing">The selection cannot match any row (for example an account id that is not a guid).</param>
public sealed record BreakdownRowFilter(
    DateOnly From,
    DateOnly To,
    bool Credit,
    IReadOnlyCollection<Guid> AccountIds,
    IReadOnlyCollection<Guid> ExcludedAccountIds,
    IReadOnlyCollection<Guid> TransferIds,
    IReadOnlyCollection<Guid> HiddenCategoryIds,
    BreakdownCategoryScope? Categories = null,
    string? Tag = null,
    bool Untagged = false,
    string? Merchant = null,
    bool UnknownMerchant = false,
    Guid? AccountId = null,
    bool MatchNothing = false);

/// <summary>One database group of plain (unsplit) transactions: only the key column of the asked-for grouping is set.</summary>
public sealed record BreakdownSqlGroup(Guid? CategoryId, string? Merchant, Guid? AccountId, string? Tags, decimal Amount, long Rows, DateOnly Newest);

/// <summary>Sum and row count of the current window and the sum of the window before it.</summary>
public sealed record BreakdownSqlTotals(decimal Current, long CurrentRows, decimal Previous);

/// <summary>
/// Database access for the spending breakdown (#160), implemented in Infrastructure with SQL <c>GROUP BY</c>. Plain transactions are
/// aggregated by the database; transactions with splits are returned as entities because each part counts in its own category.
/// </summary>
public interface IBreakdownStore
{
    /// <summary>Groups the plain transactions matching the filter by the asked-for dimension.</summary>
    Task<IReadOnlyList<BreakdownSqlGroup>> GroupAsync(BreakdownRowFilter filter, BreakdownGroupBy by, CancellationToken ct = default);

    /// <summary>Sum and count of the plain transactions on or after <paramref name="currentFrom"/>, and the sum of those before it (the filter's window spans both).</summary>
    Task<BreakdownSqlTotals> TotalsAsync(BreakdownRowFilter filter, DateOnly currentFrom, CancellationToken ct = default);

    /// <summary>Sum of the plain transactions matching the filter.</summary>
    Task<decimal> SumAsync(BreakdownRowFilter filter, CancellationToken ct = default);

    /// <summary>Daily sums of the plain transactions matching the filter.</summary>
    Task<IReadOnlyList<KeyValuePair<DateOnly, decimal>>> DailyAsync(BreakdownRowFilter filter, CancellationToken ct = default);

    /// <summary>The largest plain transactions matching the filter (amount, then date, descending).</summary>
    Task<IReadOnlyList<Transaction>> TopAsync(BreakdownRowFilter filter, int take, CancellationToken ct = default);

    /// <summary>Every transaction with splits matching the filter's common conditions (window, direction, accounts, transfers), without category or slice limits.</summary>
    Task<IReadOnlyList<Transaction>> ListSplitAsync(BreakdownRowFilter filter, CancellationToken ct = default);

    /// <summary>How many transactions (plain or split) that are in <see cref="BreakdownRowFilter.TransferIds"/> fall in the window on the visible accounts.</summary>
    Task<int> CountTransferLegsAsync(BreakdownRowFilter filter, CancellationToken ct = default);
}

internal static partial class SpendingInsightsAnalyzer
{
    /// <summary>
    /// The breakdown with the plain transactions aggregated by the database. Equal to <see cref="Analyze"/> over the same data (pinned by
    /// the equivalence tests) except that the transfer pairs come from the whole history and ties between equal amounts are broken by key.
    /// </summary>
    internal static async Task<BreakdownResult> RunSqlAsync(
        BreakdownQuery q,
        IBreakdownStore store,
        IReadOnlyDictionary<Guid, string> categoryPathById,
        IReadOnlyDictionary<Guid, string> accountNameById,
        IReadOnlySet<Guid> excludedAccountIds,
        IReadOnlySet<Guid> transferIds,
        int transferPadDays,
        CancellationToken ct)
    {
        var span = q.To.DayNumber - q.From.DayNumber + 1;
        var prevFrom = q.From.AddDays(-span);
        var byCategory = q.GroupBy == BreakdownGroupBy.Category;
        var parent = byCategory && !string.IsNullOrWhiteSpace(q.Parent) ? q.Parent : null;
        var select = string.IsNullOrWhiteSpace(q.Select) ? null : q.Select;

        var nonSpendingIds = categoryPathById.Where(p => IsNonSpending(p.Value)).Select(p => p.Key).ToList();
        var hidden = q.IncludeNonSpending ? [] : nonSpendingIds;
        var accountIds = (IReadOnlyCollection<Guid>)(q.AccountIds ?? new HashSet<Guid>());

        BreakdownCategoryScope ScopeOf(Func<string, bool> accepts)
            => new(categoryPathById.Where(p => accepts(p.Value)).Select(p => p.Key).ToList(), accepts(Uncategorized));

        var baseFilter = new BreakdownRowFilter(
            q.From, q.To, q.Income, accountIds, excludedAccountIds, transferIds, hidden,
            parent is null ? null : ScopeOf(path => InSubtree(path, parent)));

        var sliceFilter = baseFilter;
        if (select is not null)
        {
            sliceFilter = q.GroupBy switch
            {
                BreakdownGroupBy.Category => baseFilter with
                {
                    Categories = ScopeOf(path => (parent is null || InSubtree(path, parent))
                        && (InSubtree(path, select) || CategoryNode(path, q.Parent).Key.Equals(select, StringComparison.OrdinalIgnoreCase))),
                },
                BreakdownGroupBy.Tag => select == NoTag ? baseFilter with { Untagged = true } : baseFilter with { Tag = select },
                BreakdownGroupBy.Merchant => baseFilter with
                {
                    Merchant = select,
                    UnknownMerchant = select.Equals(NoMerchant, StringComparison.OrdinalIgnoreCase),
                },
                _ => Guid.TryParse(select, out var account) ? baseFilter with { AccountId = account } : baseFilter with { MatchNothing = true },
            };
        }

        // Plain transactions: the database groups them. Sequential, because the context is not safe for concurrent use.
        var groups = await store.GroupAsync(baseFilter, q.GroupBy, ct);
        var totals = await store.TotalsAsync(baseFilter with { From = prevFrom, To = q.To }, q.From, ct);
        var nonSpending = !q.IncludeNonSpending && nonSpendingIds.Count > 0
            ? await store.SumAsync(baseFilter with { Categories = new BreakdownCategoryScope(nonSpendingIds, false), HiddenCategoryIds = [] }, ct)
            : 0m;
        var daily = await store.DailyAsync(sliceFilter, ct);
        var top = await store.TopAsync(sliceFilter, MaxTransactions, ct);

        var trend = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (date, amount) in daily)
        {
            var key = BucketKey(date, q.Bucket);
            trend[key] = trend.GetValueOrDefault(key) + amount;
        }

        var plain = new Partial(
            GroupAccs(q, groups, categoryPathById, accountNameById), totals.Current, totals.Previous, (int)totals.CurrentRows, trend,
            top.Select(t => ToBreakdownTransaction(Expand(TransactionMapper.ToDto(t), categoryPathById).Single(), accountNameById)).ToList(),
            nonSpending);

        // Split transactions are few; each part counts in its own category, so they take the in-memory path.
        var splits = await store.ListSplitAsync(baseFilter with { From = prevFrom, Categories = null }, ct);
        var splitRows = splits.SelectMany(t => Expand(TransactionMapper.ToDto(t), categoryPathById)).ToList();
        var splitPartial = AnalyzeRows(q, splitRows, accountNameById);

        var transfers = transferIds.Count == 0
            ? 0
            : await store.CountTransferLegsAsync(baseFilter with { From = q.From.AddDays(-span - transferPadDays), To = q.To.AddDays(transferPadDays) }, ct);
        return Combine(q, [plain, splitPartial], transfers);
    }

    private static List<GroupAcc> GroupAccs(
        BreakdownQuery q, IReadOnlyList<BreakdownSqlGroup> groups,
        IReadOnlyDictionary<Guid, string> categoryPathById, IReadOnlyDictionary<Guid, string> accountNameById)
    {
        var merged = new Dictionary<string, (string Label, bool Children, decimal Amount, int Count, DateOnly Newest)>(StringComparer.OrdinalIgnoreCase);

        void Add(string key, string label, bool children, decimal amount, long rows, DateOnly newest)
        {
            if (!merged.TryGetValue(key, out var seen))
            {
                merged[key] = (label, children, amount, (int)rows, newest);
                return;
            }

            // The spelling shown is the newest row's (ties: ordinal), so the result does not depend on the order the database returns.
            var newer = newest > seen.Newest || (newest == seen.Newest && string.CompareOrdinal(label, seen.Label) < 0);
            merged[key] = (newer ? label : seen.Label, seen.Children || children, seen.Amount + amount, seen.Count + (int)rows, newer ? newest : seen.Newest);
        }

        foreach (var g in groups.OrderBy(g => g.Newest).ThenBy(g => g.Merchant, StringComparer.Ordinal))
        {
            switch (q.GroupBy)
            {
                case BreakdownGroupBy.Category:
                    var node = CategoryNode(CatOf(categoryPathById, g.CategoryId), q.Parent);
                    Add(node.Key, node.Label, node.HasChildren, g.Amount, g.Rows, g.Newest);
                    break;
                case BreakdownGroupBy.Tag:
                    var tags = (g.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var tag in tags.Length == 0 ? [NoTag] : tags)
                    {
                        Add(tag, tag, false, g.Amount, g.Rows, g.Newest);
                    }

                    break;
                case BreakdownGroupBy.Merchant:
                    var m = string.IsNullOrWhiteSpace(g.Merchant) ? NoMerchant : g.Merchant!;
                    Add(m, m, false, g.Amount, g.Rows, g.Newest);
                    break;
                default:
                    var id = g.AccountId.GetValueOrDefault();
                    Add(id.ToString(), accountNameById.GetValueOrDefault(id, "Account"), false, g.Amount, g.Rows, g.Newest);
                    break;
            }
        }

        return merged.Select(kv => new GroupAcc(kv.Key, kv.Value.Label, kv.Value.Children, kv.Value.Amount, kv.Value.Count)).ToList();
    }
}
