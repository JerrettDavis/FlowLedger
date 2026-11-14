using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Abstractions;

/// <summary>Repository abstraction for the Asset aggregate. All methods are tenant-scoped.</summary>
public interface IAssetRepository
{
    Task<Asset?> GetByIdAsync(AssetId id, CancellationToken ct = default);

    /// <summary>Active assets only.</summary>
    Task<IReadOnlyList<Asset>> ListAsync(CancellationToken ct = default);
    Task AddAsync(Asset asset, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
