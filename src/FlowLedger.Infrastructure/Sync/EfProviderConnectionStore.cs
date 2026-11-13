using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Sync;

internal sealed class EfProviderConnectionStore(FlowLedgerDbContext db, ITenantContext tenant) : IProviderConnectionStore
{
    public async Task<string?> GetMemberProviderIdAsync(string providerName, CancellationToken ct = default)
        => (await db.ProviderConnections.FirstOrDefaultAsync(c => c.ProviderName == providerName, ct))?.MemberProviderId;

    public async Task SaveAsync(string providerName, string memberProviderId, string institution, string status, CancellationToken ct = default)
    {
        if (await db.ProviderConnections.AnyAsync(c => c.ProviderName == providerName, ct))
        {
            return;
        }

        await db.ProviderConnections.AddAsync(
            ProviderConnectionRecord.Create(tenant.TenantId, providerName, memberProviderId, institution, status, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
    }
}
