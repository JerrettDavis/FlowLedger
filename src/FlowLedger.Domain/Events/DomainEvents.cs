using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Domain.Events;

/// <summary>Base record that satisfies IDomainEvent for all domain events in this layer.</summary>
public abstract record DomainEventBase : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    private DateTimeOffset _occurredAt = TimeProvider.System.GetUtcNow();

    /// <summary>When the event happened. Defaults to the system clock; aggregates stamp the time they were given (<see cref="StampOccurredAt"/>).</summary>
    public DateTimeOffset OccurredAt { get => _occurredAt; init => _occurredAt = value; }

    internal void StampOccurredAt(DateTimeOffset at) => _occurredAt = at;
}

// ── Post-import / post-sync events ───────────────────────────────────────────
// Every event here MUST have at least one IDomainEventHandler consumer (enforced by OutboxEventConsumerTests); an event nobody
// handles is dead weight in the outbox, so add the consumer in the same change that introduces the event.

/// <summary>Raised (through the transactional outbox) when an import committed new transactions; drives the post-import side effects.</summary>
public sealed record TransactionsImported(
    TenantId TenantId,
    AccountId AccountId,
    Guid ImportBatchId,
    int ImportedCount) : DomainEventBase;

/// <summary>Raised (through the transactional outbox) when a financial sync finished; drives the post-sync side effects.</summary>
public sealed record SyncCompleted(
    TenantId TenantId,
    int AccountsUpserted,
    int TransactionsAdded,
    int TransactionsSkipped) : DomainEventBase;
