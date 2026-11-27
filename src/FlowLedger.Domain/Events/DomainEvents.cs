using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Domain.Events;

/// <summary>Base record that satisfies IDomainEvent for all domain events in this layer.</summary>
public abstract record DomainEventBase : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>When the event happened, from the producer's <see cref="TimeProvider"/>. Required, so no event can fall back to the wall clock.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
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
