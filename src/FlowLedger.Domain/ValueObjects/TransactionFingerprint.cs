namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Deterministic fingerprint used for transaction deduplication.
/// Built from provider ID (when available), account, date, amount, and normalised description.
/// Two fingerprints that are equal represent the same real-world transaction event,
/// enabling pending-to-posted transitions without double counting.
/// </summary>
public sealed record TransactionFingerprint
{
    public string Value { get; }

    private TransactionFingerprint(string value) => Value = value;

    /// <summary>
    /// Creates a fingerprint. When <paramref name="providerTransactionId"/> is supplied it
    /// anchors the fingerprint to the provider's identity; otherwise the combination of
    /// account + date + amount + description provides a strong-enough hash for manual imports.
    /// </summary>
    public static TransactionFingerprint Create(
        AccountId accountId,
        DateOnly postedDate,
        decimal amount,
        string normalizedDescription,
        string? providerTransactionId = null)
    {
        if (string.IsNullOrWhiteSpace(normalizedDescription))
            throw new Exceptions.EmptyStringException(nameof(normalizedDescription));

        var raw = string.IsNullOrWhiteSpace(providerTransactionId)
            ? $"{accountId.Value}|{postedDate:O}|{amount:F4}|{normalizedDescription.ToUpperInvariant()}"
            : $"pid:{providerTransactionId}|{accountId.Value}";

        return new TransactionFingerprint(raw);
    }

    public override string ToString() => Value;
}
