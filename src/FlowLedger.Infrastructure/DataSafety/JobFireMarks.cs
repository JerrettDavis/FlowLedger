using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Durable record of the last cron fire time each background job was claimed for (#187). It gives two guarantees the in-memory
/// Quartz store cannot: a fire time runs at most once across worker replicas (the claim is a single conditional upsert), and a
/// restart can tell which fire times it missed while it was down.
/// </summary>
public interface IJobFireMarks
{
    /// <summary>
    /// Claims <paramref name="fireUtc"/> for <paramref name="job"/>. True for exactly one caller per fire time; false when this fire time
    /// (or a later one) was already claimed, by this process or another.
    /// </summary>
    Task<bool> TryClaimAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default);

    /// <summary>The newest claimed fire time, or null when the job has never been marked.</summary>
    Task<DateTimeOffset?> LastFireAsync(string job, CancellationToken ct = default);

    /// <summary>Records <paramref name="fireUtc"/> without running anything, only if the job has no mark yet (first start on a new database).</summary>
    Task SeedAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default);
}

/// <summary><see cref="IJobFireMarks"/> on the host-wide <c>job_fire_marks</c> table, through <see cref="HostDb"/>.</summary>
internal sealed class EfJobFireMarks(HostDb host, TimeProvider? clock = null) : IJobFireMarks
{
    public async Task<bool> TryClaimAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default)
    {
        await using var db = host.Create();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        // Atomic across replicas: the row only moves forward, and exactly one statement per fire time reports an affected row.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO job_fire_marks (job, last_fire_utc, updated_utc) VALUES ({job}, {fireUtc}, {now})
               ON CONFLICT (job) DO UPDATE SET last_fire_utc = EXCLUDED.last_fire_utc, updated_utc = EXCLUDED.updated_utc
               WHERE job_fire_marks.last_fire_utc < EXCLUDED.last_fire_utc", ct);
        return affected == 1;
    }

    public async Task<DateTimeOffset?> LastFireAsync(string job, CancellationToken ct = default)
    {
        await using var db = host.Create();
        return await db.JobFireMarks.AsNoTracking().Where(m => m.Job == job).Select(m => (DateTimeOffset?)m.LastFireUtc).FirstOrDefaultAsync(ct);
    }

    public async Task SeedAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default)
    {
        await using var db = host.Create();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO job_fire_marks (job, last_fire_utc, updated_utc) VALUES ({job}, {fireUtc}, {now}) ON CONFLICT (job) DO NOTHING", ct);
    }
}
