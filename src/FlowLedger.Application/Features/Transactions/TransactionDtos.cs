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
    string? Search = null);
