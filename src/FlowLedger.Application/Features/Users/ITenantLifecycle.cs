namespace FlowLedger.Application.Features.Users;

/// <summary>Lifecycle state of a tenant in the registry (#169).</summary>
public enum TenantStatus
{
    /// <summary>At least one login maps to the tenant.</summary>
    Active,

    /// <summary>No login maps to the tenant any more (for example after a link-code re-point). Data is kept until it is erased explicitly.</summary>
    Orphaned,

    /// <summary>All tenant data was erased. The registry row stays as the audit record.</summary>
    Deleted,
}

/// <summary>A row of the tenants registry.</summary>
public sealed record TenantInfo(Guid Id, TenantStatus Status, DateTimeOffset CreatedUtc, DateTimeOffset? OrphanedUtc, DateTimeOffset? DeletedUtc);

/// <summary>Read side of the tenants registry: makes orphaned tenants visible so they can be reviewed and offboarded.</summary>
public interface ITenantRegistry
{
    Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Tenants in the given status, oldest first.</summary>
    Task<IReadOnlyList<TenantInfo>> ListAsync(TenantStatus status, CancellationToken ct = default);
}

/// <summary>What one erasure removed, for the audit trail. Contains counts only, never data.</summary>
public sealed record TenantErasureResult(Guid TenantId, IReadOnlyDictionary<string, int> RowsDeleted, int FilesDeleted, DateTimeOffset ErasedUtc)
{
    public int TotalRows => RowsDeleted.Values.Sum();
}

/// <summary>Why an erasure was not performed.</summary>
public sealed class TenantErasureRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// Hard-deletes everything a tenant owns across every table (EF-mapped, outbox and ad-hoc tables) and any stored files, then marks the
/// registry row deleted. Refuses the household tenant. Idempotent: erasing an already erased tenant deletes nothing.
/// </summary>
public interface ITenantErasure
{
    Task<TenantErasureResult> EraseAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>A non-database store of tenant data (for example archived provider payloads) that tenant erasure must also clear.</summary>
public interface ITenantFileEraser
{
    /// <summary>Deletes everything stored for the tenant and returns how many files were removed.</summary>
    Task<int> EraseTenantAsync(Guid tenantId, CancellationToken ct = default);
}
