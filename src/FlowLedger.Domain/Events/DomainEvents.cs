using FlowLedger.Domain.Aggregates;
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

// ── Account events ───────────────────────────────────────────────────────────

/// <summary>Raised when an account is created (connected/registered) in the system.</summary>
public sealed record AccountConnected(
    AccountId AccountId,
    TenantId TenantId,
    AccountType AccountType) : DomainEventBase;

/// <summary>Raised whenever an account's current balance changes.</summary>
public sealed record AccountBalanceUpdated(
    AccountId AccountId,
    TenantId TenantId,
    Money PreviousBalance,
    Money NewBalance) : DomainEventBase;

// ── Transaction events ───────────────────────────────────────────────────────

/// <summary>Raised when an actual transaction is recorded (imported or manual).</summary>
public sealed record TransactionImported(
    TransactionId TransactionId,
    TenantId TenantId,
    AccountId AccountId,
    Money Amount,
    TransactionDirection Direction,
    TransactionSource Source) : DomainEventBase;

/// <summary>Raised when a transaction is assigned a category.</summary>
public sealed record TransactionCategorized(
    TransactionId TransactionId,
    TenantId TenantId,
    CategoryId CategoryId) : DomainEventBase;

/// <summary>Raised when an actual transaction is matched to a planned flow occurrence.</summary>
public sealed record TransactionMatchedToPlan(
    TransactionId TransactionId,
    TenantId TenantId,
    PlannedOccurrenceId PlannedOccurrenceId,
    ConfidenceScore Confidence) : DomainEventBase;

// ── Recurring flow events ────────────────────────────────────────────────────

/// <summary>Raised when a new recurring flow is registered.</summary>
public sealed record RecurringFlowCreated(
    RecurringFlowId RecurringFlowId,
    TenantId TenantId,
    AccountId AccountId,
    TransactionDirection Direction) : DomainEventBase;

/// <summary>Raised when a planned occurrence is generated for a recurring flow.</summary>
public sealed record PlannedOccurrenceGenerated(
    PlannedOccurrenceId PlannedOccurrenceId,
    RecurringFlowId RecurringFlowId,
    TenantId TenantId,
    DateOnly PlannedDate,
    Money PlannedAmount) : DomainEventBase;

// ── Asset events ─────────────────────────────────────────────────────────────

/// <summary>Raised when a physical asset is added to the tenant's holdings.</summary>
public sealed record AssetAdded(
    AssetId AssetId,
    TenantId TenantId,
    AssetCategory Category) : DomainEventBase;

/// <summary>Raised whenever an asset's current value changes through a new valuation.</summary>
public sealed record AssetRevalued(
    AssetId AssetId,
    TenantId TenantId,
    Money PreviousValue,
    Money NewValue) : DomainEventBase;
