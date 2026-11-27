using FlowLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class BalanceHistoryReader(FlowLedgerDbContext db, TimeProvider? clock = null) : IBalanceHistoryReader
{
    public Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(CancellationToken ct = default)
        => DailyAccountBalancesAsync(DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime).AddDays(-IBalanceHistoryReader.DefaultWindowDays), null, ct);

    public async Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(DateOnly from, DateOnly? to = null, CancellationToken ct = default)
    {
        var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var inWindow = db.BalanceSnapshots.AsNoTracking().Where(s => s.CapturedAt >= fromUtc);
        if (to is { } end)
        {
            var toUtc = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            inWindow = inWindow.Where(s => s.CapturedAt < toUtc);
        }

        // Collapse to one row per account per (UTC) day IN SQL: a sync every few hours writes many snapshots a day, and only the last of
        // each day is ever used, so the others are not transferred (#160).
        var lastTimes = inWindow
            .GroupBy(s => new { s.AccountId, Day = s.CapturedAt.UtcDateTime.Date })
            .Select(g => new { g.Key.AccountId, Last = g.Max(x => x.CapturedAt) });
        var lastOfDay = inWindow.Join(lastTimes, s => new { s.AccountId, s.CapturedAt }, l => new { l.AccountId, CapturedAt = l.Last }, (s, l) => s);
        var rows = await lastOfDay
            .OrderBy(s => s.CapturedAt)
            .Select(s => new { s.AccountId, s.Balance, s.Currency, s.CapturedAt })
            .ToListAsync(ct);

        // One anchor per account: its latest snapshot before the window (so "balance at the window start" needs no older history).
        var anchors = await db.BalanceSnapshots.AsNoTracking()
            .Where(s => s.CapturedAt < fromUtc
                && s.CapturedAt == db.BalanceSnapshots.Where(o => o.AccountId == s.AccountId && o.CapturedAt < fromUtc).Max(o => o.CapturedAt))
            .Select(s => new { s.AccountId, s.Balance, s.Currency, s.CapturedAt })
            .ToListAsync(ct);

        // Last snapshot per account per (UTC) day wins.
        return anchors.Concat(rows)
            .GroupBy(r => (r.AccountId, Date: DateOnly.FromDateTime(r.CapturedAt.UtcDateTime)))
            .Select(g => new BalancePoint(g.Key.Date, g.Key.AccountId, g.OrderBy(x => x.CapturedAt).Last().Balance, g.OrderBy(x => x.CapturedAt).Last().Currency))
            .OrderBy(p => p.Date)
            .ToList();
    }
}
