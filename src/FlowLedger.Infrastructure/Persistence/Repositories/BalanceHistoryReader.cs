using FlowLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class BalanceHistoryReader(FlowLedgerDbContext db) : IBalanceHistoryReader
{
    public Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(CancellationToken ct = default)
        => DailyAccountBalancesAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-IBalanceHistoryReader.DefaultWindowDays), null, ct);

    public async Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(DateOnly from, DateOnly? to = null, CancellationToken ct = default)
    {
        var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var inWindow = db.BalanceSnapshots.AsNoTracking().Where(s => s.CapturedAt >= fromUtc);
        if (to is { } end)
        {
            var toUtc = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            inWindow = inWindow.Where(s => s.CapturedAt < toUtc);
        }

        var rows = await inWindow
            .OrderBy(s => s.CapturedAt)
            .Select(s => new { s.AccountId, s.Balance, s.CapturedAt })
            .ToListAsync(ct);

        // One anchor per account: its latest snapshot before the window (so "balance at the window start" needs no older history).
        var anchors = await db.BalanceSnapshots.AsNoTracking()
            .Where(s => s.CapturedAt < fromUtc
                && s.CapturedAt == db.BalanceSnapshots.Where(o => o.AccountId == s.AccountId && o.CapturedAt < fromUtc).Max(o => o.CapturedAt))
            .Select(s => new { s.AccountId, s.Balance, s.CapturedAt })
            .ToListAsync(ct);

        // Last snapshot per account per (UTC) day wins.
        return anchors.Concat(rows)
            .GroupBy(r => (r.AccountId, Date: DateOnly.FromDateTime(r.CapturedAt.UtcDateTime)))
            .Select(g => new BalancePoint(g.Key.Date, g.Key.AccountId, g.OrderBy(x => x.CapturedAt).Last().Balance))
            .OrderBy(p => p.Date)
            .ToList();
    }
}
