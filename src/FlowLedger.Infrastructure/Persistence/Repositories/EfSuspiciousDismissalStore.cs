using FlowLedger.Application.Features.Dashboard;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>Reviewed-and-dismissed flagged transactions, tenant-filtered by the DbContext query filter (#87).</summary>
internal sealed class EfSuspiciousDismissalStore(FlowLedgerDbContext db, ITenantContext tenant) : ISuspiciousDismissalStore
{
    public async Task<IReadOnlySet<Guid>> ListIdsAsync(CancellationToken ct = default)
        => (await db.SuspiciousDismissals.AsNoTracking().Select(r => r.TransactionId).ToListAsync(ct)).ToHashSet();

    public async Task DismissAsync(Guid transactionId, string? note, CancellationToken ct = default)
        => await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO suspicious_dismissals (tenant_id, transaction_id, note) VALUES ({tenant.TenantId}, {transactionId}, {note})
               ON CONFLICT (tenant_id, transaction_id) DO UPDATE SET note = EXCLUDED.note, dismissed_utc = now()", ct);
}
