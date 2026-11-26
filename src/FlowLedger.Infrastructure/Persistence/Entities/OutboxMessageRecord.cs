namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// Transactional outbox row (#92): one per raised domain event, written in the same SaveChanges transaction as the aggregate
/// change so a committed change always has its event recorded, and a rolled-back change never does. A dispatcher claims, delivers
/// to handlers with retry and backoff, and dead-letters after the attempt cap.
/// </summary>
internal sealed class OutboxMessageRecord
{
    /// <summary>The domain event's EventId; the primary key makes recording idempotent.</summary>
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredUtc { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public Guid? LockToken { get; private set; }

    /// <summary>Comma-separated handler type names that already succeeded, so a retry never re-runs them.</summary>
    public string CompletedHandlers { get; private set; } = string.Empty;
    public DateTimeOffset? ProcessedUtc { get; private set; }
    public DateTimeOffset? DeadLetteredUtc { get; private set; }
    public string? LastError { get; private set; }

    private OutboxMessageRecord() { }

    public static OutboxMessageRecord Create(Guid id, Guid tenantId, string eventType, string payload, DateTimeOffset occurredUtc) => new()
    {
        Id = id,
        TenantId = tenantId,
        EventType = eventType,
        Payload = payload,
        OccurredUtc = occurredUtc,
        NextAttemptUtc = occurredUtc,
    };
}
