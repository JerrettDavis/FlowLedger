namespace FlowLedger.Integrations.Abstractions;

/// <summary>One outbound provider HTTP call (path only: query strings are never stored).</summary>
public sealed record ProviderCallRecord(
    string Provider,
    string Method,
    string Path,
    int? StatusCode,
    long DurationMs,
    long? ResponseBytes,
    DateTimeOffset OccurredUtc,
    string? Error = null);

/// <summary>
/// Durable, append-only record of every billable outbound provider call. Lets us measure cost,
/// enforce daily/monthly budgets and show usage in the UI.
/// </summary>
public interface IProviderCallLedger
{
    Task RecordAsync(ProviderCallRecord call, CancellationToken ct = default);

    /// <summary>Number of calls recorded for <paramref name="provider"/> at or after <paramref name="sinceUtc"/>.</summary>
    Task<long> CountSinceAsync(string provider, DateTimeOffset sinceUtc, CancellationToken ct = default);
}

/// <summary>The configured call budget is exhausted, or provider calls are paused (kill switch). Nothing was sent.</summary>
public sealed class ProviderCallBudgetExceededException : ProviderException
{
    public ProviderCallBudgetExceededException(string message)
        : base(message, "budget_exceeded")
    {
    }
}
