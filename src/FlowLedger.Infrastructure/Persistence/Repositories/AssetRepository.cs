using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class AssetRepository(FlowLedgerDbContext db) : IAssetRepository
{
    public async Task<Asset?> GetByIdAsync(AssetId id, CancellationToken ct = default)
        => await db.Assets.FirstOrDefaultAsync(a => a.Id == id.Value, ct);

    public async Task<IReadOnlyList<Asset>> ListAsync(CancellationToken ct = default)
        => await db.Assets.Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync(ct);

    public async Task AddAsync(Asset asset, CancellationToken ct = default)
        => await db.Assets.AddAsync(asset, ct);

    public Task SaveChangesAsync(CancellationToken ct = default)
        => db.SaveChangesAsync(ct);
}
