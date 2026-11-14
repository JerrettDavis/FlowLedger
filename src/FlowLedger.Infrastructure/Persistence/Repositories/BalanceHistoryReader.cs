using FlowLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class BalanceHistoryReader(FlowLedgerDbContext db) : IBalanceHistoryReader
{
    public async Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(CancellationToken ct = default)
    {
        var rows = await db.BalanceSnapshots.AsNoTracking()
            .OrderBy(s => s.CapturedAt)
            .Select(s => new { s.AccountId, s.Balance, s.CapturedAt })
            .ToListAsync(ct);

        // Last snapshot per account per (UTC) day wins.
        return rows
            .GroupBy(r => (r.AccountId, Date: DateOnly.FromDateTime(r.CapturedAt.UtcDateTime)))
            .Select(g => new BalancePoint(g.Key.Date, g.Key.AccountId, g.Last().Balance))
            .OrderBy(p => p.Date)
            .ToList();
    }
}
