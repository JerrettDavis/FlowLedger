using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Scheduling;

/// <summary>The persisted state of a job's newest claimed fire time.</summary>
/// <param name="LastFireUtc">The newest cron slot a replica has claimed.</param>
/// <param name="ClaimedUtc">When that claim was taken (the start of its lease).</param>
/// <param name="CompletedUtc">When the claiming run confirmed it finished; null while it is running or if its owner died.</param>
/// <param name="Owner">The process that holds (or held) the claim.</param>
public sealed record JobFireMark(DateTimeOffset LastFireUtc, DateTimeOffset ClaimedUtc, DateTimeOffset? CompletedUtc, string? Owner);

/// <summary>
/// Durable record of each background job's newest cron fire time (#187). It gives two guarantees the in-memory Quartz store cannot:
/// a fire time runs at most once across worker replicas (the claim is one conditional upsert), and a restart can tell which fire times
/// were missed. A claim is a LEASE: it is confirmed with <see cref="CompleteAsync"/> when the run ends, and a claim that was never
/// confirmed (its owner died) becomes reclaimable once the lease runs out, so a crashed run is re-fired instead of silently lost.
/// </summary>
public interface IJobFireMarks
{
    /// <summary>
    /// Claims <paramref name="fireUtc"/> for <paramref name="job"/> on behalf of <paramref name="owner"/>. True for exactly one caller per
    /// fire time, except that an unconfirmed claim older than <paramref name="lease"/> may be taken over. False when this fire time (or
    /// a later one) is already claimed and confirmed, or claimed by a live owner.
    /// </summary>
    Task<bool> TryClaimAsync(string job, DateTimeOffset fireUtc, string owner, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Confirms that <paramref name="owner"/> finished the run it claimed <paramref name="fireUtc"/> for. False when the claim is no longer theirs.</summary>
    Task<bool> CompleteAsync(string job, DateTimeOffset fireUtc, string owner, CancellationToken ct = default);

    /// <summary>The newest mark, or null when the job has never been marked.</summary>
    Task<JobFireMark?> GetAsync(string job, CancellationToken ct = default);

    /// <summary>Records <paramref name="fireUtc"/> as already done without running anything, only if the job has no mark yet (first start on a new database).</summary>
    Task SeedAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default);
}

/// <summary><see cref="IJobFireMarks"/> on the host-wide <c>job_fire_marks</c> table, through <see cref="HostDb"/>.</summary>
internal sealed class EfJobFireMarks(HostDb host, TimeProvider? clock = null) : IJobFireMarks
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public async Task<bool> TryClaimAsync(string job, DateTimeOffset fireUtc, string owner, TimeSpan lease, CancellationToken ct = default)
    {
        await using var db = host.Create();
        var now = Now;
        var expiredBefore = now - lease;
        // Atomic across replicas: exactly one statement per fire time reports an affected row. The row moves forward to a new slot, or
        // the SAME slot is taken over only while its claim is unconfirmed and past its lease (its owner died).
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO job_fire_marks (job, last_fire_utc, owner, claimed_utc, completed_utc, updated_utc)
               VALUES ({job}, {fireUtc}, {owner}, {now}, NULL, {now})
               ON CONFLICT (job) DO UPDATE SET last_fire_utc = EXCLUDED.last_fire_utc, owner = EXCLUDED.owner,
                   claimed_utc = EXCLUDED.claimed_utc, completed_utc = NULL, updated_utc = EXCLUDED.updated_utc
               WHERE job_fire_marks.last_fire_utc < EXCLUDED.last_fire_utc
                  OR (job_fire_marks.last_fire_utc = EXCLUDED.last_fire_utc AND job_fire_marks.completed_utc IS NULL
                      AND job_fire_marks.claimed_utc <= {expiredBefore})", ct);
        return affected == 1;
    }

    public async Task<bool> CompleteAsync(string job, DateTimeOffset fireUtc, string owner, CancellationToken ct = default)
    {
        await using var db = host.Create();
        var now = Now;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE job_fire_marks SET completed_utc = {now}, updated_utc = {now}
               WHERE job = {job} AND last_fire_utc = {fireUtc} AND owner = {owner} AND completed_utc IS NULL", ct);
        return affected == 1;
    }

    public async Task<JobFireMark?> GetAsync(string job, CancellationToken ct = default)
    {
        await using var db = host.Create();
        return await db.JobFireMarks.AsNoTracking().Where(m => m.Job == job)
            .Select(m => new JobFireMark(m.LastFireUtc, m.ClaimedUtc, m.CompletedUtc, m.Owner))
            .FirstOrDefaultAsync(ct);
    }

    public async Task SeedAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default)
    {
        await using var db = host.Create();
        var now = Now;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO job_fire_marks (job, last_fire_utc, owner, claimed_utc, completed_utc, updated_utc)
               VALUES ({job}, {fireUtc}, 'seed', {now}, {now}, {now}) ON CONFLICT (job) DO NOTHING", ct);
    }
}
