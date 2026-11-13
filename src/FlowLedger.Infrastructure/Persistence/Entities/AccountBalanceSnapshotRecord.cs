namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// Append-only point-in-time balance observed from the provider during a sync. Account.CurrentBalance
/// is overwritten each sync; this table preserves every observation and is never updated or deleted.
/// </summary>
internal sealed class AccountBalanceSnapshotRecord
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; private set; }
    public Guid AccountId { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public string ProviderAccountId { get; private set; } = string.Empty;
    public decimal Balance { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; private set; }

    private AccountBalanceSnapshotRecord() { }

    public static AccountBalanceSnapshotRecord Create(
        Guid tenantId, Guid accountId, string providerName, string providerAccountId,
        decimal balance, string currency, DateTimeOffset capturedAt) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        AccountId = accountId,
        ProviderName = providerName,
        ProviderAccountId = providerAccountId,
        Balance = balance,
        Currency = currency,
        CapturedAt = capturedAt,
    };
}
