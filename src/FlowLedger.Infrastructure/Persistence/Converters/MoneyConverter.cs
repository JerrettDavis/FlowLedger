using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowLedger.Infrastructure.Persistence.Converters;

/// <summary>
/// Value converter for <see cref="Currency"/>: stores the ISO 4217 code (e.g., "USD") as a string.
/// </summary>
internal static class CurrencyConverter
{
    public static readonly ValueConverter<Currency, string> Instance =
        new(c => c.Code, code => new Currency(code));
}
