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
        {
            throw new Exceptions.EmptyStringException(nameof(normalizedDescription));
        }

        var raw = string.IsNullOrWhiteSpace(providerTransactionId)
            ? $"{accountId.Value}|{postedDate:O}|{amount:F4}|{normalizedDescription.ToUpperInvariant()}"
            : $"pid:{providerTransactionId}|{accountId.Value}";

        return new TransactionFingerprint(raw);
    }

    /// <summary>
    /// Creates a fingerprint from a raw (un-normalised) description, normalising it with
    /// <see cref="NormalizeDescription"/>. Shared by CSV import and provider sync so both dedupe identically.
    /// </summary>
    public static TransactionFingerprint FromRawDescription(
        AccountId accountId,
        DateOnly postedDate,
        decimal amount,
        string rawDescription,
        string? providerTransactionId = null)
        => Create(accountId, postedDate, amount, NormalizeDescription(rawDescription), providerTransactionId);

    /// <summary>
    /// Stable description normalisation for fingerprinting: trim, collapse every run of whitespace to a single
    /// space, upper-case (invariant). Blank input becomes "UNKNOWN". Changing this changes every stored fingerprint.
    /// </summary>
    public static string NormalizeDescription(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "UNKNOWN";
        }

        return WhitespaceRun.Replace(raw.Trim(), " ").ToUpperInvariant();
    }

    private static readonly System.Text.RegularExpressions.Regex WhitespaceRun = new(@"\s+");

    public override string ToString() => Value;
}
