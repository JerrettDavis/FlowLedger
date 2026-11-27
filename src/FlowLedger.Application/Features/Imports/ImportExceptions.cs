using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.Imports;

// Import-flow failures that the API reports as 422 problems with the title the endpoints always used (#90).

/// <summary>An import file or request could not be processed.</summary>
public sealed class ImportFailedException(string message) : UnprocessableRequestException(message, "Import failed");

/// <summary>A manual match was refused.</summary>
public sealed class MatchFailedException(string message) : UnprocessableRequestException(message, "Match failed");

/// <summary>An unmatch was refused.</summary>
public sealed class UnmatchFailedException(string message) : UnprocessableRequestException(message, "Unmatch failed");
