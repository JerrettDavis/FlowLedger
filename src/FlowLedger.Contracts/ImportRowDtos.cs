namespace FlowLedger.Contracts;

/// <summary>Details about a row that failed to parse or validate.</summary>
public sealed record RowErrorDto(
    int RowNumber,
    string RawLine,
    string Error);
