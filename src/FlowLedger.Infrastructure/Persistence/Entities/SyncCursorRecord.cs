namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// Infrastructure-only entity that persists sync cursor bookmarks per tenant, provider, and provider account.
/// Not a domain aggregate — it has no business invariants and lives entirely within the Infrastructure layer.
/// </summary>
internal sealed class SyncCursorRecord
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public string ProviderAccountId { get; private set; } = string.Empty;
    public string CursorValue { get; private set; } = string.Empty;
    public DateTimeOffset LastSyncedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // EF Core parameterless constructor
    private SyncCursorRecord() { }

    public static SyncCursorRecord Create(
        Guid tenantId,
        string providerName,
        string providerAccountId,
        string cursorValue,
        DateTimeOffset now)
    {
        return new SyncCursorRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderName = providerName,
            ProviderAccountId = providerAccountId,
            CursorValue = cursorValue,
            LastSyncedAt = now,
            UpdatedAt = now,
        };
    }

    public void Update(string cursorValue, DateTimeOffset now)
    {
        CursorValue = cursorValue;
        LastSyncedAt = now;
        UpdatedAt = now;
    }
}
