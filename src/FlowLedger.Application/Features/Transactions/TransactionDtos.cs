using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>Response DTO for a single transaction.</summary>
public sealed record TransactionDto(
    Guid Id,
    Guid AccountId,
    decimal Amount,
    string Currency,
    string Direction,
    string Description,
    string Status,
    string Source,
    DateOnly EffectiveDate,
    DateOnly? PostedDate,
    Guid? CategoryId,
    string? MerchantName,
    string? Notes,
    string? Fingerprint,
    DateTimeOffset CreatedAt,
    string? SuggestedName = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<TransactionSplitDto>? Splits = null,
    double? CategoryConfidence = null,
    Guid? SuggestedCategoryId = null,
    double? SuggestedConfidence = null);

/// <summary>One portion of a split transaction.</summary>
public sealed record TransactionSplitDto(decimal Amount, Guid? CategoryId, string? Notes);

/// <summary>Request DTO for creating a manual transaction.</summary>
public sealed record CreateTransactionRequest(
    Guid AccountId,
    decimal Amount,
    string Currency,
    string Direction,
    string Description,
    DateOnly EffectiveDate,
    DateOnly? PostedDate,
    Guid? CategoryId,
    string? MerchantName,
    string? Notes);

/// <summary>Query parameters for listing transactions.</summary>
public sealed record ListTransactionsQuery(
    Guid? AccountId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Skip = 0,
    int Take = 100,
    bool IncludeHidden = false,
    string? Search = null);
