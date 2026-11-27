using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.Duplicates;

/// <summary>Two transactions cannot be merged as asked (mismatched, bad keep id, or both already matched).</summary>
public sealed class MergeRefusedException(string message) : UnprocessableRequestException(message, "Merge refused");
