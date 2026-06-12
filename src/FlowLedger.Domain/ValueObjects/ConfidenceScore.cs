namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// A decimal score in [0.0, 1.0] representing forecast or matching confidence.
/// Used to communicate how reliable a planned vs actual match or forecast row is.
/// </summary>
public readonly record struct ConfidenceScore
{
    public static readonly ConfidenceScore Low = new(0.25m);
    public static readonly ConfidenceScore Medium = new(0.60m);
    public static readonly ConfidenceScore High = new(0.85m);
    public static readonly ConfidenceScore Certain = new(1.0m);

    public decimal Value { get; }

    public ConfidenceScore(decimal value)
    {
        if (value is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(value), $"Confidence score must be in [0, 1], got {value}.");
        Value = value;
    }

    public bool IsHigh => Value >= 0.75m;
    public bool IsLow => Value < 0.40m;

    public override string ToString() => $"{Value:P0}";
}
