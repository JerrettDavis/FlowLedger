using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>Query parameters for listing transactions.</summary>
public sealed record ListTransactionsQuery(
    Guid? AccountId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Skip = 0,
    int Take = 100,
    bool IncludeHidden = false,
    string? Search = null,
    string? Sort = null,
    string? Dir = null,
    Guid? CategoryId = null,
    bool Uncategorized = false,
    string? Status = null,
    decimal? MinAmount = null,
    decimal? MaxAmount = null)
{
    /// <summary>The column filters (#72); null when none were asked for.</summary>
    public FlowLedger.Application.Abstractions.TransactionFilter? Filter
    {
        get
        {
            var f = new FlowLedger.Application.Abstractions.TransactionFilter(
                CategoryId, Uncategorized,
                Enum.TryParse<FlowLedger.Domain.Aggregates.TransactionStatus>(Status, ignoreCase: true, out var st) ? st : null,
                MinAmount, MaxAmount);
            return f.IsEmpty ? null : f;
        }
    }

    /// <summary>Sort keys the list endpoint accepts (case-insensitive); anything else is a 400.</summary>
    public static readonly IReadOnlyList<string> SortKeys = ["date", "merchant", "description", "amount"];

    /// <summary>The requested sort column; the newest-first default when none is given.</summary>
    public FlowLedger.Application.Abstractions.TransactionSortKey SortKey =>
        Enum.TryParse<FlowLedger.Application.Abstractions.TransactionSortKey>(Sort, ignoreCase: true, out var key)
            ? key
            : FlowLedger.Application.Abstractions.TransactionSortKey.Date;

    /// <summary>Descending unless <c>dir=asc</c> was asked for (the default order is newest first).</summary>
    public bool Descending => !string.Equals(Dir, "asc", StringComparison.OrdinalIgnoreCase);
}
