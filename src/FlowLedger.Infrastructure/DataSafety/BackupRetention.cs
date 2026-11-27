using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>A snapshot folder under the backup root: its path and when it was taken.</summary>
public sealed record SnapshotFolder(string Path, DateTimeOffset CreatedUtc)
{
    /// <summary>
    /// The folder as the snapshot service names it (<c>{prefix}-{yyyyMMdd-HHmmss}</c>, UTC). The time in the name is the age: unlike the file
    /// system's creation time it survives a copy or a restore of the backup folder. Falls back to the creation time for any other name.
    /// </summary>
    public static SnapshotFolder FromDirectory(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && name.Length >= dash + 16
            && DateTime.TryParseExact(name.AsSpan(dash + 1, 15), "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var taken))
        {
            return new SnapshotFolder(path, new DateTimeOffset(taken, TimeSpan.Zero));
        }

        return new SnapshotFolder(path, Directory.GetCreationTimeUtc(path));
    }
}

/// <summary>
/// Backup expiry policy (#189), bound from <c>Backup:Retention:*</c>. Erased tenant data stays in older backups until they expire, so the
/// window is bounded and documented (<c>docs/privacy/data-inventory-and-retention.md</c>):
/// <list type="bullet">
/// <item><c>KeepLocalSnapshots</c> (default 30, falls back to the legacy <c>Backup:KeepLocalSnapshots</c>): newest scheduled <c>backup-*</c> snapshots kept on this host;
/// a snapshot younger than <see cref="LocalMinimumAgeDays"/> is never deleted.</item>
/// <item><c>PremigrateMaxAgeDays</c> (default 90, 0 keeps them forever): <c>premigrate-*</c> snapshots older than this are removed, always keeping the newest one.</item>
/// <item><c>ResticForget</c> (default true) with <c>ResticKeepDaily/Weekly/Monthly</c> (7/4/6): after each successful off-site copy the scheduled job runs
/// <c>restic forget --prune</c> for this host's snapshots, so off-site copies expire too.</item>
/// </list>
/// Pruning only ever runs from the scheduled job after a clean run (and, with an off-site repository configured, a confirmed off-site copy); an on-demand "Back up now" never deletes anything.
/// </summary>
public sealed record BackupRetention(
    int KeepLocalSnapshots = 30,
    int PremigrateMaxAgeDays = 90,
    bool ResticForget = true,
    int ResticKeepDaily = 7,
    int ResticKeepWeekly = 4,
    int ResticKeepMonthly = 6)
{
    /// <summary>A scheduled snapshot younger than this is never deleted, whatever the keep count says.</summary>
    public const int LocalMinimumAgeDays = 7;

    public static BackupRetention Default { get; } = new();

    public static BackupRetention From(IConfiguration c)
    {
        var d = Default;
        return new BackupRetention(
            Positive(c["Backup:Retention:KeepLocalSnapshots"]) ?? Positive(c["Backup:KeepLocalSnapshots"]) ?? d.KeepLocalSnapshots,
            NonNegative(c["Backup:Retention:PremigrateMaxAgeDays"]) ?? d.PremigrateMaxAgeDays,
            !bool.TryParse(c["Backup:Retention:ResticForget"], out var forget) || forget,
            NonNegative(c["Backup:Retention:ResticKeepDaily"]) ?? d.ResticKeepDaily,
            NonNegative(c["Backup:Retention:ResticKeepWeekly"]) ?? d.ResticKeepWeekly,
            NonNegative(c["Backup:Retention:ResticKeepMonthly"]) ?? d.ResticKeepMonthly);
    }

    private static int? Positive(string? raw) => int.TryParse(raw, out var v) && v > 0 ? v : null;

    private static int? NonNegative(string? raw) => int.TryParse(raw, out var v) && v >= 0 ? v : null;

    /// <summary>True when off-site expiry is on and at least one keep rule is set (restic refuses a forget with no rule).</summary>
    public bool ResticForgetEnabled => ResticForget && (ResticKeepDaily > 0 || ResticKeepWeekly > 0 || ResticKeepMonthly > 0);

    /// <summary>
    /// Arguments after the restic executable for the expiry run. Scoped to the tag and host the backup job writes, so a repository shared with
    /// other data is never pruned. Null when expiry is off.
    /// </summary>
    public IReadOnlyList<string>? ResticForgetArguments()
    {
        if (!ResticForgetEnabled)
        {
            return null;
        }

        var args = new List<string> { "forget", "--tag", "flowledger", "--host", "flowledger" };
        AddKeep(args, "--keep-daily", ResticKeepDaily);
        AddKeep(args, "--keep-weekly", ResticKeepWeekly);
        AddKeep(args, "--keep-monthly", ResticKeepMonthly);
        args.Add("--prune");
        return args;
    }

    private static void AddKeep(List<string> args, string flag, int value)
    {
        if (value > 0)
        {
            args.Add(flag);
            args.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The local snapshot folders to delete. <paramref name="scheduled"/> are the <c>backup-*</c> folders (newest by name kept first, none younger than
    /// <see cref="LocalMinimumAgeDays"/> deleted); <paramref name="premigrate"/> are the <c>premigrate-*</c> folders (older than
    /// <see cref="PremigrateMaxAgeDays"/>, the newest always kept). Pure: the caller supplies <paramref name="nowUtc"/>.
    /// </summary>
    public IReadOnlyList<string> SelectLocalSnapshotsToDelete(
        IEnumerable<SnapshotFolder> scheduled, IEnumerable<SnapshotFolder> premigrate, DateTimeOffset nowUtc)
    {
        var doomed = new List<string>();

        if (KeepLocalSnapshots > 0)
        {
            doomed.AddRange(scheduled
                .OrderByDescending(s => System.IO.Path.GetFileName(s.Path), StringComparer.Ordinal)
                .Skip(KeepLocalSnapshots)
                .Where(s => nowUtc - s.CreatedUtc > TimeSpan.FromDays(LocalMinimumAgeDays))
                .Select(s => s.Path));
        }

        if (PremigrateMaxAgeDays > 0)
        {
            doomed.AddRange(premigrate
                .OrderByDescending(s => System.IO.Path.GetFileName(s.Path), StringComparer.Ordinal)
                .Skip(1)
                .Where(s => nowUtc - s.CreatedUtc > TimeSpan.FromDays(PremigrateMaxAgeDays))
                .Select(s => s.Path));
        }

        return doomed;
    }
}
