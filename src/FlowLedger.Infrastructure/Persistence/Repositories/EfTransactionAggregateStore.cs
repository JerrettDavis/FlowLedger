using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// SQL <c>GROUP BY</c> over the tenant's transactions for the dashboard aggregations (#160). The tenant query filter scopes every read.
/// Plain transactions and the parts of split transactions are grouped by two queries (a split transaction counts once per part, with
/// the part's amount and category), and the caller merges them.
/// </summary>
internal sealed class EfTransactionAggregateStore(FlowLedgerDbContext db) : ITransactionAggregateStore
{
    public async Task<IReadOnlyList<Transaction>> ListTransferCandidatesAsync(int max, CancellationToken ct = default)
    {
        var real = db.Transactions.AsNoTracking().Where(TransactionStatusExtensions.RealTransaction);
        const int window = TransferDetector.WindowDays;
        return await real
            .Where(a => real.Any(b =>
                b.Id != a.Id
                && b.AccountId != a.AccountId
                && b.Amount.Amount == a.Amount.Amount
                && b.Amount.Currency == a.Amount.Currency
                && (b.PostedDate ?? b.EffectiveDate) >= (a.PostedDate ?? a.EffectiveDate).AddDays(-window)
                && (b.PostedDate ?? b.EffectiveDate) <= (a.PostedDate ?? a.EffectiveDate).AddDays(window)))
            .OrderByDescending(t => t.EffectiveDate)
            .ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);
    }

    /// <summary>A transaction (or one part of a split one) reduced to what the groupings need.</summary>
    private sealed class Row
    {
        public CategoryId? Category { get; init; }
        public string Merchant { get; init; } = string.Empty;
        public DateOnly Date { get; init; }
        public DateOnly Effective { get; init; }
        public TransactionDirection Direction { get; init; }
        public decimal Amount { get; init; }
    }

    public async Task<IReadOnlyList<AggregateGroup>> GroupAsync(AggregateFilter filter, AggregateShape shape, CancellationToken ct = default)
    {
        var excludedAccounts = filter.ExcludedAccountIds.Select(id => new AccountId(id)).ToList();
        var transferIds = filter.TransferIds.ToList();
        var nonSpending = filter.NonSpendingCategoryIds.Select(CategoryId.From).ToList();

        var q = db.Transactions.AsNoTracking().Where(TransactionStatusExtensions.RealTransaction);
        if (excludedAccounts.Count > 0)
        {
            q = q.Where(t => !excludedAccounts.Contains(t.AccountId));
        }

        if (transferIds.Count > 0)
        {
            q = q.Where(t => !transferIds.Contains(t.Id));
        }

        if (filter.From is { } from)
        {
            q = q.Where(t => (t.PostedDate ?? t.EffectiveDate) >= from);
        }

        if (filter.To is { } to)
        {
            q = q.Where(t => (t.PostedDate ?? t.EffectiveDate) <= to);
        }

        if (filter.DebitsOnly)
        {
            q = q.Where(t => t.Direction == TransactionDirection.Debit);
        }

        // A category the user marked Transfer/Investments is "not spending" on the row's own category (a split row: the part's category).
        var plain = q.Where(t => !t.Splits.Any());
        if (nonSpending.Count > 0)
        {
            plain = plain.Where(t => t.CategoryId == null || !nonSpending.Contains(t.CategoryId.Value));
        }

        var plainRows = plain.Select(t => new Row
        {
            Category = t.CategoryId,
            Merchant = t.MerchantName == null || t.MerchantName.Trim() == string.Empty ? t.Description : t.MerchantName,
            Date = t.PostedDate ?? t.EffectiveDate,
            Effective = t.EffectiveDate,
            Direction = t.Direction,
            Amount = t.Amount.Amount,
        });

        var parts = q.Where(t => t.Splits.Any()).SelectMany(t => t.Splits, (t, s) => new { t, s });
        if (nonSpending.Count > 0)
        {
            parts = parts.Where(x => x.s.CategoryId == null || !nonSpending.Contains(x.s.CategoryId.Value));
        }

        var splitRows = parts.Select(x => new Row
        {
            Category = x.s.CategoryId,
            Merchant = x.t.MerchantName == null || x.t.MerchantName.Trim() == string.Empty ? x.t.Description : x.t.MerchantName,
            Date = x.t.PostedDate ?? x.t.EffectiveDate,
            Effective = x.t.EffectiveDate,
            Direction = x.t.Direction,
            Amount = x.s.Amount.Amount,
        });

        var groups = await GroupRowsAsync(plainRows, shape, ct);
        groups.AddRange(await GroupRowsAsync(splitRows, shape, ct));
        return groups;
    }

    private static async Task<List<AggregateGroup>> GroupRowsAsync(IQueryable<Row> rows, AggregateShape shape, CancellationToken ct)
    {
        switch (shape)
        {
            case AggregateShape.Category:
                {
                    var result = await rows.GroupBy(r => r.Category).Select(g => new
                    {
                        g.Key,
                        Debit = g.Sum(r => r.Direction == TransactionDirection.Debit ? r.Amount : 0m),
                        Credit = g.Sum(r => r.Direction == TransactionDirection.Credit ? r.Amount : 0m),
                        Rows = g.LongCount(),
                        Newest = g.Max(r => r.Effective),
                    }).ToListAsync(ct);
                    return result.Select(g => new AggregateGroup(g.Key?.Value, null, 0, 0, g.Debit, g.Credit, g.Rows, g.Newest)).ToList();
                }

            case AggregateShape.Merchant:
                {
                    var result = await rows.GroupBy(r => r.Merchant).Select(g => new
                    {
                        g.Key,
                        Debit = g.Sum(r => r.Direction == TransactionDirection.Debit ? r.Amount : 0m),
                        Credit = g.Sum(r => r.Direction == TransactionDirection.Credit ? r.Amount : 0m),
                        Rows = g.LongCount(),
                        Newest = g.Max(r => r.Effective),
                    }).ToListAsync(ct);
                    return result.Select(g => new AggregateGroup(null, g.Key, 0, 0, g.Debit, g.Credit, g.Rows, g.Newest)).ToList();
                }

            default:
                {
                    var result = await rows.GroupBy(r => new { r.Date.Year, r.Date.Month }).Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        Debit = g.Sum(r => r.Direction == TransactionDirection.Debit ? r.Amount : 0m),
                        Credit = g.Sum(r => r.Direction == TransactionDirection.Credit ? r.Amount : 0m),
                        Rows = g.LongCount(),
                        Newest = g.Max(r => r.Effective),
                    }).ToListAsync(ct);
                    return result.Select(g => new AggregateGroup(null, null, g.Year, g.Month, g.Debit, g.Credit, g.Rows, g.Newest)).ToList();
                }
        }
    }
}
