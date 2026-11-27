namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>One finished background job run (#187, #154). Host-wide: scheduled jobs are not per tenant. Reached only through <see cref="HostDb"/>.</summary>
internal sealed class JobRunRecord
{
    public long Id { get; private set; }
    public string Job { get; private set; } = string.Empty;
    public DateTimeOffset StartedUtc { get; private set; }
    public DateTimeOffset FinishedUtc { get; private set; }
    public bool Success { get; private set; }
    public string? Error { get; private set; }
    public int Attempt { get; private set; } = 1;

    private JobRunRecord() { }

    public static JobRunRecord Create(string job, DateTimeOffset startedUtc, DateTimeOffset finishedUtc, bool success, string? error, int attempt) =>
        new() { Job = job, StartedUtc = startedUtc, FinishedUtc = finishedUtc, Success = success, Error = error, Attempt = attempt };
}
