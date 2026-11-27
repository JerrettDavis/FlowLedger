namespace FlowLedger.Contracts;

/// <summary>A user-defined rule: a transaction whose description/merchant contains <see cref="Pattern"/> gets category <see cref="CategoryPath"/>.</summary>
public sealed record CategoryRuleDto(string Pattern, string CategoryPath);

public sealed record DuplicateSideDto(
    Guid Id, Guid AccountId, string Description, string? MerchantName, decimal Amount, string Direction, string Status,
    DateOnly EffectiveDate, Guid? CategoryId, string CategorySource, string? Notes, bool BankSynced);

public sealed record DuplicateSuggestionDto(
    string Key, string Confidence, double Score, int DaysApart, string Reason, DuplicateSideDto Keep, DuplicateSideDto Remove);

/// <summary>Summary returned after a CSV import operation.</summary>
public sealed record ImportSummaryDto(
    Guid ImportBatchId,
    int ImportedCount,
    int DuplicateCount,
    int FailedRowCount,
    int MatchedToPlanCount,
    IReadOnlyList<RowErrorDto> RowErrors);

public sealed record MerchantGroupDto(string Canonical, IReadOnlyList<MerchantOccurrence> Members, int TotalCount, double MinSimilarity, string SuggestedName);

public sealed record SimilarTransactionDto(
    Guid Id, DateOnly Date, string? Merchant, string Description, decimal Amount, string Direction, string? CategoryPath, double Similarity);

/// <summary>Summary returned after a sync run.</summary>
public sealed record SyncResult(
    int AccountsUpserted,
    int TransactionsAdded,
    int TransactionsSkipped,
    int RecurringFlowsAdded,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<Guid>? ChangedTransactionIds = null)
{
    // ChangedTransactionIds: ids of rows this sync inserted or settled in place, carried to the post-sync steps so they process only
    // those rows (#163). Null means "unknown" (a service that does not track them): callers fall back to processing the whole window.
}
