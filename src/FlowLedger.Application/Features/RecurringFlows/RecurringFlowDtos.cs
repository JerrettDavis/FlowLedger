using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.RecurringFlows;

/// <summary>Response DTO for a recurring flow.</summary>
public sealed record RecurringFlowDto(
    Guid Id,
    Guid AccountId,
    string Name,
    decimal Amount,
    string Currency,
    string Direction,
    string AmountModel,
    string RecurrenceFrequency,
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly StartDate,
    DateOnly? EndDate,
    Guid? CategoryId,
    string? Counterparty,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>Request DTO for creating a recurring flow.</summary>
public sealed record CreateRecurringFlowRequest(
    Guid AccountId,
    string Name,
    decimal Amount,
    string Currency,
    string Direction,
    string AmountModel,
    string RecurrenceFrequency,
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly StartDate,
    DateOnly? EndDate,
    Guid? CategoryId,
    string? Counterparty);

/// <summary>Request DTO for updating a recurring flow's amount/pattern.</summary>
public sealed record UpdateRecurringFlowRequest(
    decimal Amount,
    string AmountModel);
