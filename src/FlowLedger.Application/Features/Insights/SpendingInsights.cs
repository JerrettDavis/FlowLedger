using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;

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
    double? Confidence = null, string? SuggestedCategory = null, double? SuggestedConfidence = null);

public sealed record BreakdownResult(
    DateOnly From, DateOnly To, bool Income, string GroupBy, string? Parent,
    decimal Total, decimal PreviousTotal, int TransactionCount,
    IReadOnlyList<BreakdownGroup> Groups,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<BreakdownTransaction> Transactions,
    int TransfersExcluded,
    decimal NonSpendingExcluded = 0);

/// <summary>
/// Pure spending/income breakdown (Mint-style). Transfers between own accounts are excluded, splits contribute one row per
/// part, and categories are hierarchical ("Food/Groceries") so a slice can be drilled into its children.
/// </summary>
public static class SpendingInsightsAnalyzer
{
    public const string Uncategorized = "Uncategorized";
    public const string NoTag = "Untagged";
    public const string NoMerchant = "Unknown merchant";
    private const int MaxTransactions = 200;

    private sealed record Row(
        Guid Id, DateOnly Date, string Description, string? Merchant, string Category, Guid AccountId, decimal Amount, IReadOnlyList<string> Tags,
        double? Confidence = null, string? Suggested = null, double? SuggestedConfidence = null);

    public static BreakdownResult Analyze(
        BreakdownQuery q,
        IReadOnlyList<TransactionDto> all,
        IReadOnlyDictionary<Guid, string> categoryPathById,
        IReadOnlyDictionary<Guid, string> accountNameById,
        IReadOnlyDictionary<Guid, string> accountTypeById)
    {
        var transferIds = TransferDetector.Detect(all, accountTypeById);
        var wantDirection = q.Income ? "Credit" : "Debit";
        var span = q.To.DayNumber - q.From.DayNumber + 1;
        var prevFrom = q.From.AddDays(-span);
        var prevTo = q.From.AddDays(-1);

        string CatOf(Guid? id) => id is { } g && categoryPathById.TryGetValue(g, out var p) ? p : Uncategorized;

        IEnumerable<Row> Expand(TransactionDto t)
        {
            var date = t.PostedDate ?? t.EffectiveDate;
            var tags = t.Tags ?? [];
            if (t.Splits is { Count: > 0 } splits)
            {
                foreach (var s in splits)
                {
                    yield return new Row(t.Id, date, t.Description, t.MerchantName, CatOf(s.CategoryId ?? t.CategoryId), t.AccountId, s.Amount, tags);
                }
            }
            else
            {
                yield return new Row(t.Id, date, t.Description, t.MerchantName, CatOf(t.CategoryId), t.AccountId, t.Amount, tags,
                    t.CategoryConfidence, t.SuggestedCategoryId is { } sg && categoryPathById.TryGetValue(sg, out var sp) ? sp : null, t.SuggestedConfidence);
            }
        }

        var counted = all
            .Where(t => t.Status is not ("Planned" or "Skipped") && t.Direction == wantDirection)
            .Where(t => q.AccountIds is not { Count: > 0 } || q.AccountIds.Contains(t.AccountId))
            .ToList();
        var transfersExcluded = counted.Count(t => transferIds.Contains(t.Id));
        var rows = counted.Where(t => !transferIds.Contains(t.Id)).SelectMany(Expand).ToList();

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
        var total = current.Sum(r => r.Amount);

        // Selected slice narrows the trend and transaction list.
        var slice = string.IsNullOrWhiteSpace(q.Select) ? current : current.Where(r => Matches(q, r, q.Select!, accountNameById)).ToList();

        var trend = slice
            .GroupBy(r => BucketKey(r.Date, q.Bucket))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TrendPoint(g.Key, g.Sum(r => r.Amount)))
            .ToList();
        trend = FillBuckets(trend, q);

        var txs = slice
            .OrderByDescending(r => r.Amount).ThenByDescending(r => r.Date)
            .Take(MaxTransactions)
            .Select(r => new BreakdownTransaction(
                r.Id, r.Date, r.Description, r.Merchant, r.Category,
                accountNameById.GetValueOrDefault(r.AccountId, "Account"), r.Amount, r.Tags,
                r.Confidence, r.Suggested, r.SuggestedConfidence))
            .ToList();

        return new BreakdownResult(
            q.From, q.To, q.Income, q.GroupBy.ToString(), q.Parent, total, previous.Sum(r => r.Amount),
            current.Select(r => r.Id).Distinct().Count(), groups, trend, txs, transfersExcluded, nonSpending);
    }

    private static readonly string[] NonSpendingTopLevels = ["Transfer", "Transfers", "Investments", "Investment"];

    public static bool IsNonSpending(string category) =>
        NonSpendingTopLevels.Contains(category.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase);

    // ── grouping ─────────────────────────────────────────────────────────────

    private static bool InSubtree(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    private static int Depth(string? parent) =>
        string.IsNullOrWhiteSpace(parent) ? 0 : parent.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Key for a category row at the drill level: first depth+1 segments, or the path itself when it sits at the parent.</summary>
    private static (string Key, string Label, bool HasChildren) CategoryNode(string path, string? parent)
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

    private static List<BreakdownGroup> GroupRows(
        BreakdownQuery q, List<Row> rows, IReadOnlyDictionary<Guid, string> accountNames)
    {
        var total = rows.Sum(r => r.Amount);
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
            .Select(g => new BreakdownGroup(
                g.Key, g.First().Label, g.Sum(x => x.Amount), g.Select(x => x.Id).Distinct().Count(),
                total == 0 ? 0 : Math.Round(g.Sum(x => x.Amount) / total * 100m, 1), g.Any(x => x.Children)))
            .OrderByDescending(g => g.Amount)
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
        "day" => d.ToString("yyyy-MM-dd"),
        "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd"), // Monday
        _ => d.ToString("yyyy-MM"),
    };

    private static List<TrendPoint> FillBuckets(List<TrendPoint> trend, BreakdownQuery q)
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

/// <summary>Loads history and runs the analyzer.</summary>
public sealed class GetSpendingBreakdownHandler(
    ITransactionRepository repo, IAccountRepository accounts, ICategoryRepository categories)
{
    private const int MaxTransactions = 50_000;

    public async Task<BreakdownResult> HandleAsync(BreakdownQuery q, CancellationToken ct = default)
    {
        var accountEntities = await accounts.ListAsync(ct);
        var cats = (await categories.ListAsync(ct)).Select(CategoryMapper.ToDto).ToDictionary(c => c.Id, c => c.Path);
        var loaded = await repo.ListAllAsync(MaxTransactions, ct);
        var dtos = loaded.Select(TransactionMapper.ToDto).ToList();
        return SpendingInsightsAnalyzer.Analyze(
            q, dtos, cats,
            accountEntities.ToDictionary(a => a.Id, a => a.Name),
            accountEntities.ToDictionary(a => a.Id, a => a.AccountType.ToString()));
    }
}
