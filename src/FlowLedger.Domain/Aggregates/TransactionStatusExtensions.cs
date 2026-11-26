using System.Linq.Expressions;

namespace FlowLedger.Domain.Aggregates;

/// <summary>The one definition of a "real" transaction: anything other than a forecast-only (Planned) or Skipped plan row.</summary>
public static class TransactionStatusExtensions
{
    /// <summary>True when the row represents money that actually moved (or is awaiting review), not a forecast placeholder.</summary>
    public static bool IsReal(this TransactionStatus status)
        => status is not (TransactionStatus.Planned or TransactionStatus.Skipped);

    /// <summary>
    /// The same rule as <see cref="IsReal"/> as an expression, so it can be used inside EF queries (<c>Where(RealTransaction)</c>)
    /// and a database filter can never drift from the in-memory one.
    /// </summary>
    public static readonly Expression<Func<Transaction, bool>> RealTransaction
        = t => t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped;

    /// <summary>
    /// String form for DTOs that carry the status as text (the enum name). An unrecognised value counts as real,
    /// matching the long-standing "not Planned and not Skipped" rule.
    /// </summary>
    public static bool IsRealStatus(string? status)
        => !Enum.TryParse<TransactionStatus>(status, ignoreCase: false, out var parsed) || parsed.IsReal();
}
