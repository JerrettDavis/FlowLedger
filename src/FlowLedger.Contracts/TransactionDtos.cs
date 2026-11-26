namespace FlowLedger.Contracts;

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

/// <summary>A transaction surfaced for attention, with human-readable reasons.</summary>
public sealed record FlaggedTransactionDto(TransactionDto Transaction, IReadOnlyList<string> Reasons);

/// <summary>At-a-glance activity feed across all accounts.</summary>
public sealed record DashboardActivityDto(
    IReadOnlyList<TransactionDto> RecentPosted,
    IReadOnlyList<TransactionDto> Pending,
    decimal PendingNetAmount,
    IReadOnlyList<TransactionDto> Large,
    decimal LargeThreshold,
    IReadOnlyList<FlaggedTransactionDto> Suspicious,
    decimal MoneyInLast30Days,
    decimal MoneyOutLast30Days,
    int TransfersExcluded = 0);
