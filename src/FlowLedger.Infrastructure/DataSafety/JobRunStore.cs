using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>One finished background job run, as persisted.</summary>
public sealed record JobRunEntry(string Job, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, bool Success, string? Error, int Attempt);

/// <summary>Durable history of background job runs (the worker writes it, the API reads it for the Data page).</summary>
public interface IJobRunStore
{
    Task AppendAsync(JobRunEntry run, CancellationToken ct = default);

    /// <summary>Most recent first.</summary>
    Task<IReadOnlyList<JobRunEntry>> RecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// EF-backed <see cref="IJobRunStore"/> (<c>job_runs</c>, host-wide, through <see cref="HostDb"/>; part of every snapshot).
/// Rows older than <see cref="RetentionDays"/> are deleted on insert.
/// </summary>
internal sealed class EfJobRunStore(HostDb host) : IJobRunStore
{
    public const int RetentionDays = 90;

    public async Task AppendAsync(JobRunEntry run, CancellationToken ct = default)
    {
        await using var db = host.Create();
        db.JobRuns.Add(JobRunRecord.Create(run.Job, run.StartedUtc, run.FinishedUtc, run.Success, Truncate(run.Error, 1000), run.Attempt));
        await db.SaveChangesAsync(ct);

        var cutoff = run.FinishedUtc.AddDays(-RetentionDays);
        await db.JobRuns.Where(r => r.FinishedUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<JobRunEntry>> RecentAsync(int limit, CancellationToken ct = default)
    {
        await using var db = host.Create();
        var rows = await db.JobRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return rows.Select(r => new JobRunEntry(r.Job, r.StartedUtc, r.FinishedUtc, r.Success, r.Error, r.Attempt)).ToList();
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
