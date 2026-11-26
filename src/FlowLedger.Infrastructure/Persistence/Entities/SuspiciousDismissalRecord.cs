namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A flagged transaction the tenant reviewed and dismissed, with an optional note (#87: formerly an ad-hoc Npgsql table).</summary>
internal sealed class SuspiciousDismissalRecord
{
    public Guid TenantId { get; private set; }
    public Guid TransactionId { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset DismissedUtc { get; private set; }

    private SuspiciousDismissalRecord() { }
}
