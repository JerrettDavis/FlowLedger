namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Single place for rounding money. Uses <see cref="MidpointRounding.AwayFromZero"/> (what people expect from
/// currency: 0.125 becomes 0.13) rather than the banker's rounding that <see cref="Math.Round(decimal, int)"/> applies by default.
/// </summary>
public static class MoneyRounding
{
    public static decimal Round(decimal amount, int decimals = 2)
        => Math.Round(amount, decimals, MidpointRounding.AwayFromZero);
}
