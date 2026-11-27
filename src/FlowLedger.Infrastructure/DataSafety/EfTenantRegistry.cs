using FlowLedger.Application.Features.Users;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// The tenants registry (#169, #154) through EF. A tenant used to exist only implicitly as a <c>tenant_id</c> value on rows, so a tenant
/// whose last login was re-pointed by a link code became invisible. Now every tenant has a row; the user directory keeps it in step
/// (active / orphaned) and erasure marks it deleted but never removes it. The key column is <c>id</c> (not <c>tenant_id</c>) because
/// this table is the registry itself, not tenant-owned data; it is listed in <see cref="HostWideEntities"/>.
/// </summary>
internal sealed class EfTenantRegistry(HostDb host) : ITenantRegistry
{
    public async Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = host.Create();
        var row = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<TenantInfo>> ListAsync(TenantStatus status, CancellationToken ct = default)
    {
        var wanted = ToDb(status);
        await using var db = host.Create();
        var rows = await db.Tenants.AsNoTracking().Where(t => t.Status == wanted).OrderBy(t => t.CreatedUtc).ThenBy(t => t.Id).ToListAsync(ct);
        return rows.Select(Read).ToList();
    }

    internal static string ToDb(TenantStatus status) => status.ToString().ToLowerInvariant();

    private static TenantInfo Read(TenantRegistryRecord r) => new(
        r.Id,
        Enum.TryParse<TenantStatus>(r.Status, ignoreCase: true, out var s) ? s : TenantStatus.Active,
        r.CreatedUtc,
        r.OrphanedUtc,
        r.DeletedAt);
}
