namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Strongly-typed identifier for a Tenant aggregate root.
/// Wrapping Guid prevents accidental cross-aggregate ID confusion.
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.NewGuid());
    public static TenantId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for an Account aggregate root.</summary>
public readonly record struct AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.NewGuid());
    public static AccountId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a Transaction aggregate root.</summary>
public readonly record struct TransactionId(Guid Value)
{
    public static TransactionId New() => new(Guid.NewGuid());
    public static TransactionId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a RecurringFlow aggregate root.</summary>
public readonly record struct RecurringFlowId(Guid Value)
{
    public static RecurringFlowId New() => new(Guid.NewGuid());
    public static RecurringFlowId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a PlannedFlowOccurrence entity.</summary>
public readonly record struct PlannedOccurrenceId(Guid Value)
{
    public static PlannedOccurrenceId New() => new(Guid.NewGuid());
    public static PlannedOccurrenceId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identifier for a Category entity.</summary>
public readonly record struct CategoryId(Guid Value)
{
    public static CategoryId New() => new(Guid.NewGuid());
    public static CategoryId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
