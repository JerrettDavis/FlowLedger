namespace FlowLedger.Application.Features.Categories;

/// <summary>One row's category before a change: enough to put it back (Undo). <paramref name="CategoryId"/> null means it had none.</summary>
/// <param name="Manual">True when the user chose the previous category by hand, false when the auto-categorizer set it.</param>
public sealed record CategoryRestore(Guid TransactionId, Guid? CategoryId, bool Manual, double? Confidence);

/// <summary>How many rows a category change touched, and what each looked like before.</summary>
public sealed record CategoryChangeResult(int Changed, IReadOnlyList<CategoryRestore> Restore);
