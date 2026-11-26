namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// Provider currency handling (#124). A provider that omits or garbles a currency must never be read as USD: that silently
/// mislabels a EUR/GBP/JPY balance and corrupts net worth and forecasts. An account with no usable currency is skipped (and logged);
/// a transaction without its own currency inherits its account's, and is skipped when even that is unknown.
/// </summary>
public static class ProviderCurrency
{
    /// <summary>The upper-cased ISO-4217-shaped code (three letters), or null when the provider gave none or something unusable.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        return trimmed.Length == 3 && trimmed.All(char.IsAsciiLetter) ? trimmed.ToUpperInvariant() : null;
    }
}

/// <summary>A provider row has no usable currency and none can be inherited; the caller skips the row instead of assuming USD.</summary>
public sealed class UnknownCurrencyException(string message) : Exception(message);
