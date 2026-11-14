namespace FlowLedger.Application.Features.BillSheet;

/// <summary>A user-correctable problem with a sheet request. Maps to HTTP 400.</summary>
public sealed class SheetOperationException(string message) : Exception(message);

public sealed record SheetCellDto(bool Paid, decimal? Amount, DateOnly? PaidOn, string Source);

public sealed record SheetRowDto(
    Guid Id,
    string Name,
    string? Payer,
    bool IsDebt,
    bool IsPerpetual,
    bool IsOptional,
    bool IsBusiness,
    bool AutoPay,
    int? DueDay,
    decimal Estimated,
    decimal? Actual,
    string? Service,
    string? AccountRefMasked,
    string? MatchText,
    bool NeedsMatchText,
    IReadOnlyDictionary<string, SheetCellDto> Cells);

/// <summary>Sums as in the workbook: Total, Optional, Required, Personal, Business.</summary>
public sealed record SheetTotalsLine(decimal Total, decimal Optional, decimal Required, decimal Personal, decimal Business);

public sealed record SheetTotalsDto(SheetTotalsLine Estimated, SheetTotalsLine Actual);

public sealed record SheetSectionDto(string Name, IReadOnlyList<SheetRowDto> Rows, SheetTotalsDto Totals);

/// <summary>Progress for the selected month: how much is paid and what is still to come.</summary>
public sealed record SheetMonthSummaryDto(
    string Month,
    int PaidCount,
    int TotalCount,
    decimal PaidAmount,
    decimal RemainingEstimated);

public sealed record SheetViewDto(
    IReadOnlyList<string> Months,
    string SelectedMonth,
    IReadOnlyList<SheetSectionDto> Sections,
    SheetTotalsDto GrandTotals,
    SheetMonthSummaryDto Summary);

public sealed record SaveBillRequest(
    string Section,
    string Name,
    string? Payer,
    bool IsDebt,
    bool IsPerpetual,
    bool IsOptional,
    bool IsBusiness,
    bool AutoPay,
    int? DueDay,
    decimal Estimated,
    decimal? Actual,
    string? Service,
    string? AccountRef,
    string? MatchText);

/// <summary>Manual toggle of a cell. <c>Paid=false</c> records a manual "not paid" that suppresses automation.</summary>
public sealed record SetPaymentRequest(bool Paid, decimal? Amount);

public sealed record ImportSheetRequest(string Csv, bool Replace);

public sealed record SheetImportResult(int Sections, int Bills, int PaidMarks);

public sealed record SheetRefreshResult(int Marked, int Cleared);
