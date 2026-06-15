namespace FlowLedger.Integrations.Simulated;

/// <summary>
/// Lightweight seed descriptor for a recurring flow produced by the simulated provider.
/// All values are primitive so this type has no dependency on the domain layer.
/// The Infrastructure sync service maps these into <c>RecurringFlow</c> aggregates.
/// </summary>
/// <param name="ProviderAccountId">Provider account id this flow is attached to.</param>
/// <param name="Name">Display name for the flow (max 200 chars).</param>
/// <param name="Amount">Positive amount in <paramref name="CurrencyCode"/>.</param>
/// <param name="CurrencyCode">ISO 4217 currency code, e.g. "USD".</param>
/// <param name="Direction">"Credit" or "Debit".</param>
/// <param name="FrequencyName">Matches <c>RecurrenceFrequency</c> enum names: "Monthly", "EveryNWeeks", etc.</param>
/// <param name="DayOfMonth">Day-of-month (1–31) for Monthly patterns; null otherwise.</param>
/// <param name="IntervalWeeks">Interval in weeks for EveryNWeeks patterns; null otherwise.</param>
/// <param name="AnchorDayOfWeek">Day-of-week name for Weekly/EveryNWeeks patterns; null otherwise.</param>
/// <param name="Counterparty">Optional merchant/payee name.</param>
public sealed record SimulatedRecurringFlowSeed(
    string ProviderAccountId,
    string Name,
    decimal Amount,
    string CurrencyCode,
    string Direction,
    string FrequencyName,
    int? DayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    string? Counterparty);
