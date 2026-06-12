namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Immutable money value object. Uses <see cref="decimal"/> for exact arithmetic —
/// no floating-point errors. All arithmetic operations guard against currency mismatches.
/// Negative amounts are allowed (e.g. account balances, liability values);
/// use <see cref="GuardPositive"/> when a positive-only constraint is required.
/// </summary>
public sealed record Money
{
    public decimal Amount { get; }
    public Currency Currency { get; }

    public Money(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Amount = amount;
        Currency = currency;
    }

    public Money(decimal amount, string currencyCode)
        : this(amount, new Currency(currencyCode)) { }

    // ── Factories ────────────────────────────────────────────────────────────

    public static Money Zero(Currency currency) => new(0m, currency);
    public static Money Zero(string currencyCode) => new(0m, currencyCode);

    // ── Arithmetic ───────────────────────────────────────────────────────────

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Negate() => new(-Amount, Currency);

    public Money Multiply(decimal factor) => new(Amount * factor, Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    // ── Guards ───────────────────────────────────────────────────────────────

    /// <summary>Throws <see cref="Exceptions.NegativeOrZeroAmountException"/> if amount ≤ 0.</summary>
    public Money GuardPositive(string context)
    {
        if (Amount <= 0m)
            throw new Exceptions.NegativeOrZeroAmountException(context);
        return this;
    }

    // ── Comparison ───────────────────────────────────────────────────────────

    public bool IsPositive => Amount > 0m;
    public bool IsNegative => Amount < 0m;
    public bool IsZero => Amount == 0m;

    public static bool operator >(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return left.Amount > right.Amount;
    }

    public static bool operator <(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return left.Amount < right.Amount;
    }

    public static bool operator >=(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return left.Amount >= right.Amount;
    }

    public static bool operator <=(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return left.Amount <= right.Amount;
    }

    // ── Operator overloads ───────────────────────────────────────────────────

    public static Money operator +(Money left, Money right) => left.Add(right);
    public static Money operator -(Money left, Money right) => left.Subtract(right);
    public static Money operator -(Money m) => m.Negate();
    public static Money operator *(Money m, decimal factor) => m.Multiply(factor);
    public static Money operator *(decimal factor, Money m) => m.Multiply(factor);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnsureSameCurrency(Money other)
    {
        if (!Currency.Equals(other.Currency))
            throw new Exceptions.CurrencyMismatchException(Currency.Code, other.Currency.Code);
    }

    public override string ToString() => $"{Amount:F2} {Currency.Code}";
}
