using FlowLedger.Application.Features.Insights;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// SQL <c>GROUP BY</c> for the spending breakdown (#160). The tenant query filter scopes every read. Plain transactions (no splits) are
/// aggregated here; transactions with splits are returned as entities for the caller to expand, because each part counts in its own category.
/// </summary>
internal sealed class EfBreakdownStore(FlowLedgerDbContext db) : IBreakdownStore
{
    /// <summary>Real transactions of the wanted direction in the window on the visible accounts, optionally minus transfer legs.</summary>
    private IQueryable<Transaction> Common(BreakdownRowFilter f, bool excludeTransfers)
    {
        var excludedAccounts = f.ExcludedAccountIds.Select(id => new AccountId(id)).ToList();
        var onlyAccounts = f.AccountIds.Select(id => new AccountId(id)).ToList();
        var transferIds = f.TransferIds.ToList();
        var direction = f.Credit ? TransactionDirection.Credit : TransactionDirection.Debit;
        var from = f.From;
        var to = f.To;

        var q = db.Transactions.AsNoTracking().Where(TransactionStatusExtensions.RealTransaction)
            .Where(t => t.Direction == direction && (t.PostedDate ?? t.EffectiveDate) >= from && (t.PostedDate ?? t.EffectiveDate) <= to);
        if (excludedAccounts.Count > 0)
        {
            q = q.Where(t => !excludedAccounts.Contains(t.AccountId));
        }

        if (onlyAccounts.Count > 0)
        {
            q = q.Where(t => onlyAccounts.Contains(t.AccountId));
        }

        if (excludeTransfers && transferIds.Count > 0)
        {
            q = q.Where(t => !transferIds.Contains(t.Id));
        }

        return q;
    }

    /// <summary>The plain transactions the filter selects (hidden categories, category scope and the slice applied).</summary>
    private IQueryable<Transaction> Plain(BreakdownRowFilter f)
    {
        var q = Common(f, excludeTransfers: true).Where(t => !t.Splits.Any());
        if (f.MatchNothing)
        {
            return q.Where(t => false);
        }

        var hidden = f.HiddenCategoryIds.Select(CategoryId.From).ToList();
        if (hidden.Count > 0)
        {
            q = q.Where(t => t.CategoryId == null || !hidden.Contains(t.CategoryId.Value));
        }

        if (f.Categories is { } scope)
        {
            var ids = scope.Ids.Select(CategoryId.From).ToList();
            q = scope.IncludeUncategorized
                ? q.Where(t => t.CategoryId == null || ids.Contains(t.CategoryId.Value))
                : q.Where(t => t.CategoryId != null && ids.Contains(t.CategoryId.Value));
        }

        if (f.Untagged)
        {
            q = q.Where(t => EF.Property<string?>(t, "_tags") == null || EF.Property<string?>(t, "_tags") == string.Empty);
        }
        else if (f.Tag is { } tag)
        {
            var pattern = "%," + LikeEscape(tag) + ",%";
            q = q.Where(t => EF.Property<string?>(t, "_tags") != null && EF.Functions.ILike("," + EF.Property<string?>(t, "_tags") + ",", pattern, "\\"));
        }

        if (f.Merchant is { } merchant)
        {
            var pattern = LikeEscape(merchant);
            q = f.UnknownMerchant
                ? q.Where(t => t.MerchantName == null || t.MerchantName.Trim() == string.Empty || EF.Functions.ILike(t.MerchantName, pattern, "\\"))
                : q.Where(t => t.MerchantName != null && EF.Functions.ILike(t.MerchantName, pattern, "\\"));
        }

        if (f.AccountId is { } account)
        {
            var id = new AccountId(account);
            q = q.Where(t => t.AccountId == id);
        }

        return q;
    }

    /// <summary>Escapes LIKE wildcards so a user-chosen name matches literally (case-insensitively through ILIKE).</summary>
    private static string LikeEscape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<IReadOnlyList<BreakdownSqlGroup>> GroupAsync(BreakdownRowFilter filter, BreakdownGroupBy by, CancellationToken ct = default)
    {
        var rows = Plain(filter).Select(t => new
        {
            t.CategoryId,
            t.MerchantName,
            t.AccountId,
            Tags = EF.Property<string?>(t, "_tags"),
            Amount = t.Amount.Amount,
            Effective = t.EffectiveDate,
        });

        switch (by)
        {
            case BreakdownGroupBy.Category:
                return (await rows.GroupBy(r => r.CategoryId)
                        .Select(g => new { g.Key, Amount = g.Sum(r => r.Amount), Rows = g.LongCount(), Newest = g.Max(r => r.Effective) })
                        .ToListAsync(ct))
                    .Select(g => new BreakdownSqlGroup(g.Key?.Value, null, null, null, g.Amount, g.Rows, g.Newest)).ToList();
            case BreakdownGroupBy.Merchant:
                return (await rows.GroupBy(r => r.MerchantName)
                        .Select(g => new { g.Key, Amount = g.Sum(r => r.Amount), Rows = g.LongCount(), Newest = g.Max(r => r.Effective) })
                        .ToListAsync(ct))
                    .Select(g => new BreakdownSqlGroup(null, g.Key, null, null, g.Amount, g.Rows, g.Newest)).ToList();
            case BreakdownGroupBy.Tag:
                return (await rows.GroupBy(r => r.Tags)
                        .Select(g => new { g.Key, Amount = g.Sum(r => r.Amount), Rows = g.LongCount(), Newest = g.Max(r => r.Effective) })
                        .ToListAsync(ct))
                    .Select(g => new BreakdownSqlGroup(null, null, null, g.Key, g.Amount, g.Rows, g.Newest)).ToList();
            default:
                return (await rows.GroupBy(r => r.AccountId)
                        .Select(g => new { g.Key, Amount = g.Sum(r => r.Amount), Rows = g.LongCount(), Newest = g.Max(r => r.Effective) })
                        .ToListAsync(ct))
                    .Select(g => new BreakdownSqlGroup(null, null, g.Key.Value, null, g.Amount, g.Rows, g.Newest)).ToList();
        }
    }

    public async Task<BreakdownSqlTotals> TotalsAsync(BreakdownRowFilter filter, DateOnly currentFrom, CancellationToken ct = default)
    {
        var result = await Plain(filter)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Current = g.Sum(t => (t.PostedDate ?? t.EffectiveDate) >= currentFrom ? t.Amount.Amount : 0m),
                CurrentRows = g.LongCount(t => (t.PostedDate ?? t.EffectiveDate) >= currentFrom),
                Previous = g.Sum(t => (t.PostedDate ?? t.EffectiveDate) < currentFrom ? t.Amount.Amount : 0m),
            })
            .SingleOrDefaultAsync(ct);
        return result is null ? new BreakdownSqlTotals(0m, 0, 0m) : new BreakdownSqlTotals(result.Current, result.CurrentRows, result.Previous);
    }

    public async Task<decimal> SumAsync(BreakdownRowFilter filter, CancellationToken ct = default)
        => await Plain(filter).SumAsync(t => (decimal?)t.Amount.Amount, ct) ?? 0m;

    public async Task<IReadOnlyList<KeyValuePair<DateOnly, decimal>>> DailyAsync(BreakdownRowFilter filter, CancellationToken ct = default)
        => (await Plain(filter)
                .GroupBy(t => t.PostedDate ?? t.EffectiveDate)
                .Select(g => new { g.Key, Amount = g.Sum(t => t.Amount.Amount) })
                .ToListAsync(ct))
            .Select(g => KeyValuePair.Create(g.Key, g.Amount)).ToList();

    public async Task<IReadOnlyList<Transaction>> TopAsync(BreakdownRowFilter filter, int take, CancellationToken ct = default)
        => await Plain(filter)
            .OrderByDescending(t => t.Amount.Amount).ThenByDescending(t => t.PostedDate ?? t.EffectiveDate).ThenBy(t => t.Id)
            .Take(take)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Transaction>> ListSplitAsync(BreakdownRowFilter filter, CancellationToken ct = default)
        => await Common(filter, excludeTransfers: true).Where(t => t.Splits.Any()).ToListAsync(ct);

    public async Task<int> CountTransferLegsAsync(BreakdownRowFilter filter, CancellationToken ct = default)
    {
        var ids = filter.TransferIds.ToList();
        return await Common(filter, excludeTransfers: false).Where(t => ids.Contains(t.Id)).CountAsync(ct);
    }
}
