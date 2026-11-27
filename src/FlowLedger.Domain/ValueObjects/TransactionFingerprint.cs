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
        string? providerTransactionId = null,
        int occurrence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);

        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            throw new Exceptions.EmptyStringException(nameof(normalizedDescription));
        }

        var raw = string.IsNullOrWhiteSpace(providerTransactionId)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{accountId.Value}|{postedDate:O}|{amount:F4}|{normalizedDescription.ToUpperInvariant()}")
            : $"pid:{providerTransactionId}|{accountId.Value}";

        // Occurrence 0 keeps the historical value so stored fingerprints stay valid; the Nth identical
        // provider-less row on the same day (n >= 1) gets a "|n" suffix so genuine repeats stay distinct.
        if (occurrence > 0 && string.IsNullOrWhiteSpace(providerTransactionId))
        {
            raw = $"{raw}|{occurrence}";
        }

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
        string? providerTransactionId = null,
        int occurrence = 0)
        => Create(accountId, postedDate, amount, NormalizeDescription(rawDescription), providerTransactionId, occurrence);

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
