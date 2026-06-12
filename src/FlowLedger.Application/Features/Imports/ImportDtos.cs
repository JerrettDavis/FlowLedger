namespace FlowLedger.Application.Features.Imports;

// ── Column mapping ────────────────────────────────────────────────────────────

/// <summary>
/// Describes which CSV column index maps to which transaction field.
/// Indices are 0-based. Optional columns use null to indicate absence.
/// </summary>
public sealed record CsvColumnMapping(
    int DateColumnIndex,
    int AmountColumnIndex,
    int DescriptionColumnIndex,
    int? MerchantColumnIndex = null,
    int? CategoryColumnIndex = null,
    char Delimiter = ',',
    /// <summary>
    /// Format strings to try when parsing dates (e.g. "yyyy-MM-dd", "M/d/yyyy").
    /// Null means try a standard set of common formats.
    /// </summary>
    string[]? DateFormats = null,
    bool HasHeaderRow = true);

// ── Import command ────────────────────────────────────────────────────────────

/// <summary>Command for importing transactions from CSV text.</summary>
public sealed record ImportTransactionsCommand(
    Guid AccountId,
    string CsvContent,
    CsvColumnMapping Mapping);

// ── Import result ─────────────────────────────────────────────────────────────

/// <summary>Summary returned after a CSV import operation.</summary>
public sealed record ImportSummaryDto(
    Guid ImportBatchId,
    int ImportedCount,
    int DuplicateCount,
    int FailedRowCount,
    int MatchedToPlanCount,
    IReadOnlyList<RowErrorDto> RowErrors);

/// <summary>Details about a row that failed to parse or validate.</summary>
public sealed record RowErrorDto(
    int RowNumber,
    string RawLine,
    string Error);

// ── Match command ─────────────────────────────────────────────────────────────

/// <summary>Manually match an actual transaction to a planned occurrence.</summary>
public sealed record MatchTransactionCommand(
    Guid TransactionId,
    Guid PlannedOccurrenceId);

/// <summary>Remove a match link, reverting both sides to their pre-match status.</summary>
public sealed record UnmatchTransactionCommand(Guid TransactionId);

/// <summary>Result of a match or unmatch operation.</summary>
public sealed record MatchResultDto(
    Guid TransactionId,
    Guid? PlannedOccurrenceId,
    string TransactionStatus,
    string? OccurrenceStatus,
    decimal? ConfidenceScore,
    decimal? AmountVariancePennies,
    int? DateVarianceDays);

// ── NeedsReview ───────────────────────────────────────────────────────────────

/// <summary>A candidate match suggestion waiting for user review.</summary>
public sealed record MatchSuggestionDto(
    Guid TransactionId,
    Guid PlannedOccurrenceId,
    decimal ConfidenceScore,
    decimal ActualAmount,
    decimal PlannedAmount,
    string Currency,
    DateOnly ActualDate,
    DateOnly PlannedDate,
    string Description,
    string RecurringFlowName);
