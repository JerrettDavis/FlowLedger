namespace FlowLedger.Domain.Exceptions;

/// <summary>
/// Base class for all domain invariant violations. Domain exceptions are thrown
/// synchronously during aggregate construction or command execution and should
/// never be swallowed silently.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message) { }
}

/// <summary>Thrown when a money operation involves mismatched currencies.</summary>
public sealed class CurrencyMismatchException : DomainException
{
    public CurrencyMismatchException(string left, string right)
        : base($"Cannot perform operation between currencies '{left}' and '{right}'.") { }
}

/// <summary>Thrown when a monetary amount is expected to be positive but is not.</summary>
public sealed class NegativeOrZeroAmountException : DomainException
{
    public NegativeOrZeroAmountException(string context)
        : base($"Amount must be greater than zero in context: {context}.") { }
}

/// <summary>Thrown when a required string argument is null or whitespace.</summary>
public sealed class EmptyStringException : DomainException
{
    public EmptyStringException(string fieldName)
        : base($"'{fieldName}' cannot be null or empty.") { }
}

/// <summary>Thrown when a transition between transaction statuses is illegal.</summary>
public sealed class InvalidStatusTransitionException : DomainException
{
    public InvalidStatusTransitionException(string from, string to, string entity)
        : base($"Invalid status transition from '{from}' to '{to}' on {entity}.") { }
}

/// <summary>Thrown when a recurring flow occurrence date is outside the flow's active window.</summary>
public sealed class OccurrenceDateOutOfRangeException : DomainException
{
    public OccurrenceDateOutOfRangeException(DateOnly date, DateOnly start, DateOnly? end)
        : base($"Occurrence date {date:O} is outside the active window [{start:O}, {end?.ToString("O") ?? "open"}].") { }
}

/// <summary>Thrown when a transaction is matched to a plan occurrence that already has a match.</summary>
public sealed class OccurrenceAlreadyMatchedException : DomainException
{
    public OccurrenceAlreadyMatchedException(Guid occurrenceId)
        : base($"Planned occurrence {occurrenceId} is already matched to an actual transaction.") { }
}

/// <summary>Thrown when an account balance update is attempted with a negative balance on a non-credit account.</summary>
public sealed class InvalidBalanceException : DomainException
{
    public InvalidBalanceException(string message) : base(message) { }
}

/// <summary>Thrown when a split transaction's parts do not sum to the total.</summary>
public sealed class SplitAmountMismatchException : DomainException
{
    public SplitAmountMismatchException(decimal partsTotal, decimal transactionTotal, string currency)
        : base($"Split parts total {partsTotal} {currency} does not equal transaction total {transactionTotal} {currency}.") { }
}
