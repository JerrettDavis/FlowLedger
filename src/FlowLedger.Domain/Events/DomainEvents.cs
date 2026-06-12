using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Domain.Events;

/// <summary>Base record that satisfies IDomainEvent for all domain events in this layer.</summary>
public abstract record DomainEventBase : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
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
