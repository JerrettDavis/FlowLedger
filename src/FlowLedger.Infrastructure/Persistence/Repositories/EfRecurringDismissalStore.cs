using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>Dismissed recurring suggestions, tenant-filtered by the DbContext query filter (#87).</summary>
internal sealed class EfRecurringDismissalStore(FlowLedgerDbContext db, ITenantContext tenant) : IRecurringDismissalStore
{
    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken ct = default)
        => (await db.RecurringDismissals.AsNoTracking().Select(r => r.SuggestionKey).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    public async Task AddAsync(string key, CancellationToken ct = default)
        => await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO recurring_dismissals (tenant_id, suggestion_key) VALUES ({tenant.TenantId}, {key}) ON CONFLICT DO NOTHING", ct);
}
