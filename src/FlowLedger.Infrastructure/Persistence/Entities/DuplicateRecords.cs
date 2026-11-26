namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A merge tombstone plus the snapshot that lets it be undone (#154: formerly an ad-hoc table).</summary>
internal sealed class MergedTransactionRecord
{
    public Guid TenantId { get; private set; }
    public Guid DuplicateId { get; private set; }
    public Guid SurvivorId { get; private set; }
    public string? Fingerprint { get; private set; }
    public DateTimeOffset MergedUtc { get; private set; }
    public bool Auto { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string Snapshot { get; private set; } = string.Empty;

    private MergedTransactionRecord() { }
}

/// <summary>A pair the owner said is not a duplicate (#154).</summary>
internal sealed class DuplicateDismissalRecord
{
    public Guid TenantId { get; private set; }
    public string PairKey { get; private set; } = string.Empty;
    public DateTimeOffset DismissedUtc { get; private set; }

    private DuplicateDismissalRecord() { }
}
