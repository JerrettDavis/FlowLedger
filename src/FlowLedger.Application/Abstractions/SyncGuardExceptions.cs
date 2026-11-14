namespace FlowLedger.Application.Abstractions;

/// <summary>A sync was refused because the previous one finished too recently (API-cost control). Nothing was called.</summary>
public sealed class SyncThrottledException : Exception
{
    public TimeSpan RetryAfter { get; }

    public SyncThrottledException(TimeSpan retryAfter, DateTimeOffset lastSyncedAt)
        : base($"Last synced {lastSyncedAt:u}. Provider data is only re-fetched at most every few minutes to avoid API costs; try again in {Math.Ceiling(retryAfter.TotalMinutes)} min.")
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>A sync for this tenant is already running; concurrent syncs are refused instead of double-calling the provider.</summary>
public sealed class SyncAlreadyRunningException : Exception
{
    public SyncAlreadyRunningException()
        : base("A sync is already in progress for this tenant.")
    {
    }
}
