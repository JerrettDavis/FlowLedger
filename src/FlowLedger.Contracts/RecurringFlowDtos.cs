namespace FlowLedger.Contracts;

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
    DateTimeOffset CreatedAt,
    decimal MonthlyAmount = 0m);

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

/// <summary>A detected recurring pattern, as shown to the user.</summary>
public sealed record RecurringSuggestionDto(
    string Key,
    Guid AccountId,
    string AccountName,
    string Name,
    string Direction,
    decimal Amount,
    string Currency,
    string AmountModel,
    string Frequency,
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly LastSeen,
    DateOnly NextExpected,
    int Occurrences,
    double Confidence,
    bool WouldAutoAdd);
