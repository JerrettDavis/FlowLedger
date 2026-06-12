namespace FlowLedger.Integrations.Simulated;

/// <summary>
/// Configuration knobs for the Simulated provider.
/// Bind from configuration section "SimulatedProvider" or configure in tests.
/// </summary>
public sealed class SimulatedProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SimulatedProvider";

    /// <summary>
    /// Fixed random seed mixed with tenant id to produce deterministic data.
    /// Change this in tests to get a different dataset while preserving reproducibility.
    /// </summary>
    public int BaseSeed { get; set; } = 42;

    /// <summary>
    /// Number of months of transaction history to generate per account.
    /// </summary>
    public int HistoryMonths { get; set; } = 6;

    /// <summary>
    /// Fraction of operations that should fail with a <see cref="Abstractions.TransientProviderException"/>.
    /// Range 0.0–1.0; 0.0 = never fail (default).
    /// </summary>
    public double FailureRate { get; set; } = 0.0;

    /// <summary>
    /// Artificial delay in milliseconds added to every provider call.
    /// Useful for testing resilience and timeout handling.
    /// </summary>
    public int LatencyMs { get; set; } = 0;

    /// <summary>
    /// After this many successful operations in a session, all subsequent calls
    /// throw <see cref="Abstractions.RateLimitedProviderException"/>.
    /// 0 means no rate limiting.
    /// </summary>
    public int RateLimitAfter { get; set; } = 0;
}
