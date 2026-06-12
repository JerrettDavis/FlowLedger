using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// EF Core implementation of <see cref="ISyncCursorStore"/>.
/// Upserts cursor records keyed by (TenantId, ProviderName, ProviderAccountId).
/// Tenant scoping is applied via the global query filter on the DbContext and by
/// writing the current tenant id on insert.
/// </summary>
internal sealed class EfSyncCursorStore : ISyncCursorStore
{
    private readonly FlowLedgerDbContext _db;
    private readonly ITenantContext _tenant;

    public EfSyncCursorStore(FlowLedgerDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<string> GetAsync(
        string providerName,
        string providerAccountId,
        CancellationToken ct = default)
    {
        var record = await _db.SyncCursors
            .FirstOrDefaultAsync(
                r => r.ProviderName == providerName && r.ProviderAccountId == providerAccountId,
                ct);

        return record?.CursorValue ?? string.Empty;
    }

    public async Task SetAsync(
        string providerName,
        string providerAccountId,
        string cursorValue,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var record = await _db.SyncCursors
            .FirstOrDefaultAsync(
                r => r.ProviderName == providerName && r.ProviderAccountId == providerAccountId,
                ct);

        if (record is null)
        {
            record = SyncCursorRecord.Create(
                _tenant.TenantId,
                providerName,
                providerAccountId,
                cursorValue,
                now);
            await _db.SyncCursors.AddAsync(record, ct);
        }
        else
        {
            record.Update(cursorValue, now);
        }

        await _db.SaveChangesAsync(ct);
    }
}
