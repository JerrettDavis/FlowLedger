namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A provider-side account a connector reported (#154: formerly an ad-hoc Npgsql table).</summary>
internal sealed class ProviderAccountRecord
{
    public Guid TenantId { get; private set; }
    public string ConnectorId { get; private set; } = string.Empty;
    public string ProviderAccountId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string AccountType { get; private set; } = string.Empty;
    public decimal Balance { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTimeOffset LastSeenUtc { get; private set; }

    private ProviderAccountRecord() { }
}
