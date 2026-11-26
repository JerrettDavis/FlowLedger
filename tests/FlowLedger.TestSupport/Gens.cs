using CsCheck;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.TestSupport;

/// <summary>CsCheck generators for the domain value objects. Shrinking works through every generator here.</summary>
public static class Gens
{
    private static readonly string[] CurrencyCodes = ["USD", "EUR", "GBP", "JPY"];

    public static Gen<Currency> Currency { get; } = Gen.OneOf(CurrencyCodes).Select(c => new Currency(c));

    /// <summary>Decimal with 2 fractional digits in roughly +/- 10 million; the realistic money range.</summary>
    public static Gen<decimal> Amount { get; } = Gen.Long[-1_000_000_000L, 1_000_000_000L].Select(c => c / 100m);

    /// <summary>Strictly positive amount with 2 fractional digits.</summary>
    public static Gen<decimal> PositiveAmount { get; } = Gen.Long[1L, 1_000_000_000L].Select(c => c / 100m);

    public static Gen<Money> Money { get; } = Gen.Select(Amount, Currency, (a, c) => new Money(a, c));

    /// <summary>Dates from 2019-01-01 through 2032-12-31 (covers several leap days and every month length).</summary>
    public static Gen<DateOnly> Date { get; } = Gen.Int[0, 5113].Select(d => new DateOnly(2019, 1, 1).AddDays(d));

    public static Gen<DayOfWeek> DayOfWeekGen { get; } = Gen.Int[0, 6].Select(i => (DayOfWeek)i);

    public static Gen<RecurrencePattern> Pattern { get; } = Gen.OneOf(
        Gen.Const(RecurrencePattern.Daily()),
        DayOfWeekGen.Select(RecurrencePattern.Weekly),
        Gen.Select(Gen.Int[2, 8], DayOfWeekGen, RecurrencePattern.EveryNWeeks),
        Gen.Int[1, 31].Select(RecurrencePattern.Monthly),
        Gen.Select(Gen.Int[1, 30], Gen.Int[1, 31], (a, b) => (a, b))
            .Where(t => t.a < t.b)
            .Select(t => RecurrencePattern.TwiceMonthly(t.a, t.b)),
        Gen.Const(RecurrencePattern.LastBusinessDay()));
}
