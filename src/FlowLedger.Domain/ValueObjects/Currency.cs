namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// ISO 4217 currency code (e.g. "USD", "EUR"). Stored as an immutable record to enable
/// value equality. Only non-empty, three-letter uppercase codes are accepted.
/// </summary>
public sealed record Currency
{
    public static readonly Currency Usd = new("USD");
    public static readonly Currency Eur = new("EUR");
    public static readonly Currency Gbp = new("GBP");

    public string Code { get; }

    public Currency(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new Exceptions.EmptyStringException(nameof(code));
        }

        var upper = code.Trim().ToUpperInvariant();
        if (upper.Length != 3 || !upper.All(char.IsLetter))
        {
            throw new ArgumentException($"'{code}' is not a valid ISO 4217 currency code (must be 3 ASCII letters).", nameof(code));
        }

        Code = upper;
    }

    public override string ToString() => Code;
}
