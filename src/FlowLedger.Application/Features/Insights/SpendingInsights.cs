using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Insights;

public enum BreakdownGroupBy { Category, Tag, Merchant, Account }

public sealed record BreakdownQuery(
    DateOnly From,
    DateOnly To,
    IReadOnlySet<Guid>? AccountIds = null,
    bool Income = false,
    BreakdownGroupBy GroupBy = BreakdownGroupBy.Category,
    string? Parent = null,            // category drill-down: only children of this path
    string? Select = null,            // a clicked slice: narrows trend + transactions to it
    string Bucket = "month",          // month | week | day
    bool IncludeNonSpending = false); // Transfer / Investments categories are excluded unless asked for

public sealed record BreakdownGroup(string Key, string Label, decimal Amount, int Count, decimal Percent, bool HasChildren);
public sealed record TrendPoint(string Bucket, decimal Amount);
public sealed record BreakdownTransaction(
    Guid Id, DateOnly Date, string Description, string? Merchant, string Category, string Account, decimal Amount, IReadOnlyList<string> Tags,
    double? Confidence = null, string? SuggestedCategory = null, double? SuggestedConfidence = null, uint Version = 0);

public sealed record BreakdownResult(
    DateOnly From, DateOnly To, bool Income, string GroupBy, string? Parent,
    decimal Total, decimal PreviousTotal, int TransactionCount,
    IReadOnlyList<BreakdownGroup> Groups,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<BreakdownTransaction> Transactions,
    int TransfersExcluded,
    decimal NonSpendingExcluded = 0,
    bool Partial = false);   // true: the period holds more rows than the processing cap, so the oldest were left out

/// <summary>
/// Pure spending/income breakdown (Mint-style). Transfers between own accounts are excluded, splits contribute one row per
/// part, and categories are hierarchical ("Food/Groceries") so a slice can be drilled into its children.
/// </summary>
internal static partial class SpendingInsightsAnalyzer
{
    public const string Uncategorized = "Uncategorized";
    public const string NoTag = "Untagged";
    public const string NoMerchant = "Unknown merchant";
    internal const int MaxTransactions = 200;

    internal sealed record Row(
        Guid Id, DateOnly Date, string Description, string? Merchant, string Category, Guid AccountId, decimal Amount, IReadOnlyList<string> Tags,
        double? Confidence = null, string? Suggested = null, double? SuggestedConfidence = null, uint Version = 0);

    /// <summary>One group's running figures before the final percentages.</summary>
    internal sealed record GroupAcc(string Key, string Label, bool HasChildren, decimal Amount, int Count);

    /// <summary>
    /// Everything the result needs from one set of rows. The in-memory path produces one for all rows; the SQL path (#160) produces one
    /// from <c>GROUP BY</c> results for the plain transactions and one in memory for the (few) split ones, and <see cref="Combine"/>
    /// merges them.
    /// </summary>
    internal sealed record Partial(
        IReadOnlyList<GroupAcc> Groups, decimal Total, decimal PreviousTotal, int Count,
        IReadOnlyDictionary<string, decimal> Trend, IReadOnlyList<BreakdownTransaction> Transactions, decimal NonSpending);

    public static BreakdownResult Analyze(
        BreakdownQuery q,
        IReadOnlyList<TransactionDto> all,
        IReadOnlyDictionary<Guid, string> categoryPathById,
        IReadOnlyDictionary<Guid, string> accountNameById,
        IReadOnlyDictionary<Guid, string> accountTypeById,
        IReadOnlySet<Guid>? excludedAccountIds = null)
    {
        var transferIds = TransferDetector.Detect(all, accountTypeById);
        if (excludedAccountIds is { Count: > 0 })
        {
            all = all.Where(t => !excludedAccountIds.Contains(t.AccountId)).ToList();
        }

        var wantDirection = q.Income ? nameof(TransactionDirection.Credit) : nameof(TransactionDirection.Debit);
        var counted = all
            .Where(t => FlowLedger.Domain.Aggregates.TransactionStatusExtensions.IsRealStatus(t.Status) && t.Direction == wantDirection)
            .Where(t => q.AccountIds is not { Count: > 0 } || q.AccountIds.Contains(t.AccountId))
            .ToList();
        var transfersExcluded = counted.Count(t => transferIds.Contains(t.Id));
        var rows = counted.Where(t => !transferIds.Contains(t.Id)).SelectMany(t => Expand(t, categoryPathById)).ToList();

        return Combine(q, [AnalyzeRows(q, rows, accountNameById)], transfersExcluded);
    }

    internal static string CatOf(IReadOnlyDictionary<Guid, string> categoryPathById, Guid? id)
        => id is { } g && categoryPathById.TryGetValue(g, out var p) ? p : Uncategorized;

    /// <summary>A transaction as the rows it contributes: one per split part, else one.</summary>
    internal static IEnumerable<Row> Expand(TransactionDto t, IReadOnlyDictionary<Guid, string> categoryPathById)
    {
        var date = t.PostedDate ?? t.EffectiveDate;
        var tags = t.Tags ?? [];
        if (t.Splits is { Count: > 0 } splits)
        {
            foreach (var s in splits)
            {
                yield return new Row(t.Id, date, t.Description, t.MerchantName, CatOf(categoryPathById, s.CategoryId ?? t.CategoryId), t.AccountId, s.Amount, tags,
                    Version: t.Version);
            }
        }
        else
        {
            yield return new Row(t.Id, date, t.Description, t.MerchantName, CatOf(categoryPathById, t.CategoryId), t.AccountId, t.Amount, tags,
                t.CategoryConfidence, t.SuggestedCategoryId is { } sg && categoryPathById.TryGetValue(sg, out var sp) ? sp : null, t.SuggestedConfidence, t.Version);
        }
    }

    internal static BreakdownTransaction ToBreakdownTransaction(Row r, IReadOnlyDictionary<Guid, string> accountNameById) => new(
        r.Id, r.Date, r.Description, r.Merchant, r.Category,
        accountNameById.GetValueOrDefault(r.AccountId, "Account"), r.Amount, r.Tags,
        r.Confidence, r.Suggested, r.SuggestedConfidence, r.Version);

    /// <summary>The breakdown of already expanded, transfer-free rows of the requested direction.</summary>
    internal static Partial AnalyzeRows(BreakdownQuery q, List<Row> rows, IReadOnlyDictionary<Guid, string> accountNameById)
    {
        var span = q.To.DayNumber - q.From.DayNumber + 1;
        var prevFrom = q.From.AddDays(-span);
        var prevTo = q.From.AddDays(-1);

        // Moving money to a brokerage or an unmatched account is not spending/income. Hidden by default (Mint-style).
        decimal nonSpending = 0;
        if (!q.IncludeNonSpending)
        {
            nonSpending = rows.Where(r => r.Date >= q.From && r.Date <= q.To && IsNonSpending(r.Category)).Sum(r => r.Amount);
            rows = rows.Where(r => !IsNonSpending(r.Category)).ToList();
        }

        var current = rows.Where(r => r.Date >= q.From && r.Date <= q.To).ToList();
        var previous = rows.Where(r => r.Date >= prevFrom && r.Date <= prevTo).ToList();

        // Category drill-down: restrict to the parent subtree.
        if (q.GroupBy == BreakdownGroupBy.Category && !string.IsNullOrWhiteSpace(q.Parent))
        {
            current = current.Where(r => InSubtree(r.Category, q.Parent!)).ToList();
            previous = previous.Where(r => InSubtree(r.Category, q.Parent!)).ToList();
        }

        var groups = GroupRows(q, current, accountNameById);

        // Selected slice narrows the trend and transaction list.
        var slice = string.IsNullOrWhiteSpace(q.Select) ? current : current.Where(r => Matches(q, r, q.Select!, accountNameById)).ToList();
        var trend = slice.GroupBy(r => BucketKey(r.Date, q.Bucket)).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount), StringComparer.Ordinal);
        var txs = slice
            .OrderByDescending(r => r.Amount).ThenByDescending(r => r.Date)
            .Take(MaxTransactions)
            .Select(r => ToBreakdownTransaction(r, accountNameById))
            .ToList();

        return new Partial(groups, current.Sum(r => r.Amount), previous.Sum(r => r.Amount), current.Select(r => r.Id).Distinct().Count(), trend, txs, nonSpending);
    }

    /// <summary>Merges partials over disjoint transactions into the result (groups merged case-insensitively, then ranked).</summary>
    internal static BreakdownResult Combine(BreakdownQuery q, IReadOnlyList<Partial> parts, int transfersExcluded)
    {
        var total = parts.Sum(p => p.Total);
        var merged = new Dictionary<string, GroupAcc>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in parts.SelectMany(p => p.Groups))
        {
            merged[g.Key] = merged.TryGetValue(g.Key, out var seen)
                ? seen with { Amount = seen.Amount + g.Amount, Count = seen.Count + g.Count, HasChildren = seen.HasChildren || g.HasChildren }
                : g;
        }

        var groups = merged.Values
            .Select(g => new BreakdownGroup(g.Key, g.Label, g.Amount, g.Count, total == 0 ? 0 : Math.Round(g.Amount / total * 100m, 1), g.HasChildren))
            .OrderByDescending(g => g.Amount)
            .ToList();

        var trendSums = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (bucket, amount) in parts.SelectMany(p => p.Trend))
        {
            trendSums[bucket] = trendSums.GetValueOrDefault(bucket) + amount;
        }

        var trend = FillBuckets(trendSums.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new TrendPoint(t.Key, t.Value)).ToList(), q);
        var txs = parts.SelectMany(p => p.Transactions)
            .OrderByDescending(t => t.Amount).ThenByDescending(t => t.Date)
            .Take(MaxTransactions)
            .ToList();

        return new BreakdownResult(
            q.From, q.To, q.Income, q.GroupBy.ToString(), q.Parent, total, parts.Sum(p => p.PreviousTotal), parts.Sum(p => p.Count),
            groups, trend, txs, transfersExcluded, parts.Sum(p => p.NonSpending));
    }

    private static readonly string[] NonSpendingTopLevels = ["Transfer", "Transfers", "Investments", "Investment"];

    public static bool IsNonSpending(string category) =>
        NonSpendingTopLevels.Contains(category.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase);

    // ── grouping ─────────────────────────────────────────────────────────────

    internal static bool InSubtree(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    private static int Depth(string? parent) =>
        string.IsNullOrWhiteSpace(parent) ? 0 : parent.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Key for a category row at the drill level: first depth+1 segments, or the path itself when it sits at the parent.</summary>
    internal static (string Key, string Label, bool HasChildren) CategoryNode(string path, string? parent)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var d = Depth(parent);
        if (segs.Length > d)
        {
            var key = string.Join('/', segs.Take(d + 1));
            return (key, segs[d], segs.Length > d + 1);
        }

        return (path, segs.Length > 0 ? segs[^1] + " (other)" : path, false);
    }

    private static List<GroupAcc> GroupRows(
        BreakdownQuery q, List<Row> rows, IReadOnlyDictionary<Guid, string> accountNames)
    {
        IEnumerable<(string Key, string Label, bool Children, decimal Amount, Guid Id)> flat = q.GroupBy switch
        {
            BreakdownGroupBy.Category => rows.Select(r =>
            {
                var n = CategoryNode(r.Category, q.Parent);
                return (n.Key, n.Label, n.HasChildren, r.Amount, r.Id);
            }),
            BreakdownGroupBy.Tag => rows.SelectMany(r => (r.Tags.Count == 0 ? [NoTag] : r.Tags)
                .Select(tag => (tag, tag, false, r.Amount, r.Id))),
            BreakdownGroupBy.Merchant => rows.Select(r =>
            {
                var m = string.IsNullOrWhiteSpace(r.Merchant) ? NoMerchant : r.Merchant!;
                return (m, m, false, r.Amount, r.Id);
            }),
            _ => rows.Select(r =>
            {
                var n = accountNames.GetValueOrDefault(r.AccountId, "Account");
                return (r.AccountId.ToString(), n, false, r.Amount, r.Id);
            }),
        };

        return flat
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupAcc(g.Key, g.First().Label, g.Any(x => x.Children), g.Sum(x => x.Amount), g.Select(x => x.Id).Distinct().Count()))
            .ToList();
    }

    private static bool Matches(BreakdownQuery q, Row r, string select, IReadOnlyDictionary<Guid, string> accountNames) => q.GroupBy switch
    {
        BreakdownGroupBy.Category => InSubtree(r.Category, select) ||
                                     CategoryNode(r.Category, q.Parent).Key.Equals(select, StringComparison.OrdinalIgnoreCase),
        BreakdownGroupBy.Tag => select == NoTag
            ? r.Tags.Count == 0
            : r.Tags.Contains(select, StringComparer.OrdinalIgnoreCase),
        BreakdownGroupBy.Merchant => (string.IsNullOrWhiteSpace(r.Merchant) ? NoMerchant : r.Merchant!)
            .Equals(select, StringComparison.OrdinalIgnoreCase),
        _ => r.AccountId.ToString().Equals(select, StringComparison.OrdinalIgnoreCase),
    };

    // ── buckets ──────────────────────────────────────────────────────────────

    public static string BucketKey(DateOnly d, string bucket) => bucket switch
    {
        "day" => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), // Monday
        _ => d.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    };

    internal static List<TrendPoint> FillBuckets(List<TrendPoint> trend, BreakdownQuery q)
    {
        var have = trend.ToDictionary(t => t.Bucket, t => t.Amount, StringComparer.Ordinal);
        var result = new List<TrendPoint>();
        var cursor = q.From;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (cursor <= q.To)
        {
            var key = BucketKey(cursor, q.Bucket);
            if (seen.Add(key))
            {
                result.Add(new TrendPoint(key, have.GetValueOrDefault(key)));
            }

            cursor = cursor.AddDays(1);
            if (result.Count > 400)
            {
                break;
            }
        }

        return result;
    }
}

/// <summary>
/// Loads what the breakdown needs and runs it. With the SQL store registered (#160) the plain transactions are grouped by the database
/// (a constant number of statements, whatever the history size) and only the split ones are read as rows; without it (or for an inverted
/// range) the history is loaded and analyzed in memory.
/// </summary>
public sealed class GetSpendingBreakdownHandler(
    ITransactionRepository repo, IAccountRepository accounts, ICategoryRepository categories,
    IBreakdownStore? store = null, ITransactionAggregateStore? transferSource = null)
{
    private const int MaxTransactions = TransactionQuota.ProcessingCap;
    private const int TransferPadDays = 14;

    public async Task<BreakdownResult> HandleAsync(BreakdownQuery q, CancellationToken ct = default)
    {
        var accountEntities = await accounts.ListAsync(ct);
        var cats = (await categories.ListAsync(ct)).Select(CategoryMapper.ToDto).ToDictionary(c => c.Id, c => c.Path);
        var accountNames = accountEntities.ToDictionary(a => a.Id, a => a.Name);
        var accountTypes = accountEntities.ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var excluded = await AccountVisibility.ExcludedIdsAsync(accounts, q.AccountIds, ct);

        if (store is not null && transferSource is not null && q.To >= q.From)
        {
            var candidates = await transferSource.ListTransferCandidatesAsync(MaxTransactions, ct);
            var transferIds = TransferDetector.Detect(candidates.Select(TransactionMapper.ToDto).ToList(), accountTypes);
            return await SpendingInsightsAnalyzer.RunSqlAsync(q, store, cats, accountNames, excluded, transferIds, TransferPadDays, ct);
        }

        // Only the selected period and the equally long prior period are compared; pad both ends so transfer pairs that straddle the edge still match.
        var span = Math.Max(1, q.To.DayNumber - q.From.DayNumber + 1);
        var loaded = q.To < q.From
            ? await repo.ListAllAsync(MaxTransactions, ct)
            : await repo.ListAsync(null, q.From.AddDays(-span - TransferPadDays), q.To.AddDays(TransferPadDays), 0, MaxTransactions, ct);
        var dtos = loaded.Select(TransactionMapper.ToDto).ToList();
        var result = SpendingInsightsAnalyzer.Analyze(q, dtos, cats, accountNames, accountTypes, excluded);
        return TransactionQuota.IsPartial(loaded.Count) ? result with { Partial = true } : result;
    }
}
