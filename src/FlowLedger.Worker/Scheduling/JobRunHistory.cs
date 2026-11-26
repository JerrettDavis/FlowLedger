using System.Collections.Concurrent;

namespace FlowLedger.Worker.Scheduling;

public sealed record JobRunRecord(string Job, DateTimeOffset StartedUtc, TimeSpan Duration, bool Success, string? Error);

/// <summary>
/// Bounded in-memory record of recent job runs (process lifetime only). This is the seam a persisted job-run history
/// would write through; persisting needs a table, which is tracked separately.
/// </summary>
public sealed class JobRunHistory
{
    private const int Capacity = 200;
    private readonly ConcurrentQueue<JobRunRecord> _runs = new();

    public void Add(JobRunRecord run)
    {
        _runs.Enqueue(run);
        while (_runs.Count > Capacity && _runs.TryDequeue(out _))
        {
        }
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<JobRunRecord> Recent(int limit = 50) => _runs.Reverse().Take(limit).ToList();
}
