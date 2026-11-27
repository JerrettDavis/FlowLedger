using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// A single portion of a split transaction, attributed to a specific category.
/// This is a value-like entity owned entirely by its parent Transaction.
/// </summary>
public sealed class TransactionSplit
{
    public Money Amount { get; private set; }
    public CategoryId? CategoryId { get; private set; }
    public string? Notes { get; private set; }

    private TransactionSplit()
    {
        // EF Core parameterless constructor — fields initialised by EF.
        Amount = null!;
    }

    public TransactionSplit(Money amount, CategoryId? categoryId = null, string? notes = null)
    {
        amount.GuardPositive(nameof(amount));
        Amount = amount;
        CategoryId = categoryId;
        Notes = notes?.Trim();
    }
}

/// <summary>Provenance of a transaction's category.</summary>
public enum CategorySource
{
    /// <summary>Chosen by the user (or existing data of unknown origin): never overwritten automatically.</summary>
    Manual,
    /// <summary>Filled in by the auto-categorizer: may be replaced when the user corrects a merchant.</summary>
    Auto,
}

public enum TransactionDirection
{
    Debit,
    Credit,
}

public enum TransactionStatus
{
    /// <summary>Forecast-only row. No actual transaction yet.</summary>
    Planned,
    /// <summary>Imported pending transaction, not yet cleared.</summary>
    Pending,
    /// <summary>Cleared and confirmed by the financial institution.</summary>
    Posted,
    /// <summary>Linked to a planned occurrence via the match engine.</summary>
    Matched,
    /// <summary>User has confirmed this transaction is correct.</summary>
    Reconciled,
    /// <summary>A planned row the user has skipped for this cycle.</summary>
    Skipped,
    /// <summary>Ignored (e.g. internal transfer leg already counted elsewhere).</summary>
    Ignored,
    /// <summary>Needs user review (e.g. uncategorised, duplicate suspect).</summary>
    NeedsReview,
}

public enum TransactionSource
{
    Manual,
    CsvImport,
    OfxImport,
    MxAggregation,
    RecurringPlan,
    ApiImport,
}
