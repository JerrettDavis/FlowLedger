using System.Diagnostics;
using System.Text.Json;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Settings for scheduled backups, bound from <c>Backup:*</c> (+ <c>ObjectStorage:RootPath</c>).</summary>
public sealed record BackupSettings(
    string BackupRoot,
    string? ObjectsDir,
    string? ResticRepository,
    string? ResticPasswordFile,
    string ResticExecutable,
    int KeepLocalSnapshots,
    bool DrillAfterBackup,
    BackupRetention? Retention = null)
{
    public bool OffsiteConfigured => !string.IsNullOrWhiteSpace(ResticRepository);

    /// <summary>The expiry policy (#189). When none was supplied, the defaults apply with this record's <see cref="KeepLocalSnapshots"/>.</summary>
    public BackupRetention EffectiveRetention => Retention ?? BackupRetention.Default with { KeepLocalSnapshots = KeepLocalSnapshots };

    public static BackupSettings From(IConfiguration c) => new(
        c["Backup:Directory"] ?? DatabaseSnapshotService.DefaultBackupRoot(),
        c["ObjectStorage:RootPath"],
        c["Backup:Restic:Repository"],
        c["Backup:Restic:PasswordFile"],
        c["Backup:Restic:Executable"] ?? "restic",
        BackupRetention.From(c).KeepLocalSnapshots,
        !bool.TryParse(c["Backup:DrillAfterBackup"], out var d) || d,
        BackupRetention.From(c));
}

/// <summary>
/// What a restore drill found, kept with the backup run so the Data page can show it: every table's row count in the backup, after the
/// restore, and live at drill time, plus the raw provider-payload archive check.
/// </summary>
public sealed record RestoreDrillSummary(
    IReadOnlyList<DrillTableResult> Tables, bool RawArchiveChecked, int RawObjectsVerified, int RawObjectsSealed, IReadOnlyList<string> Problems)
{
    /// <summary>The Application-layer view the Data page and the status endpoint use.</summary>
    public static FlowLedger.Application.Features.DataSafety.RestoreDrillView? ToView(RestoreDrillSummary? drill) =>
        drill is null
            ? null
            : new FlowLedger.Application.Features.DataSafety.RestoreDrillView(
                drill.Tables.Select(t => new FlowLedger.Application.Features.DataSafety.DrillTableView(t.Table, t.ExpectedRows, t.RestoredRows, t.LiveRows, t.Ok)).ToList(),
                drill.RawArchiveChecked, drill.RawObjectsVerified, drill.RawObjectsSealed, drill.Problems);
}

public sealed record BackupRunResult(
    bool Ok,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string? SnapshotDir,
    long Rows,
    bool? OffsiteOk,
    string? OffsiteDetail,
    bool? DrillOk,
    string? Error,
    RestoreDrillSummary? Drill = null);

/// <summary>Durable history of backup runs (<c>backup_runs</c>, host-wide, through EF; shown in the UI).</summary>
public sealed class BackupRunStore
{
    private readonly HostDb _host;

    public BackupRunStore(string connectionString) => _host = new HostDb(connectionString);

    public async Task AddAsync(BackupRunResult r, CancellationToken ct = default)
    {
        await using var db = _host.Create();
        db.BackupRuns.Add(BackupRunRecord.Create(
            r.StartedUtc, r.FinishedUtc, r.Ok, r.SnapshotDir, r.Rows, r.OffsiteOk, r.OffsiteDetail, r.DrillOk, r.Error,
            r.Drill is null ? null : JsonSerializer.Serialize(r.Drill, DrillJson)));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BackupRunResult>> RecentAsync(int limit, CancellationToken ct = default)
    {
        await using var db = _host.Create();
        var rows = await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(limit).ToListAsync(ct);
        return rows.Select(r => new BackupRunResult(
            r.Ok, r.StartedUtc, r.FinishedUtc, r.SnapshotDir, r.Rows, r.OffsiteOk, r.OffsiteDetail, r.DrillOk, r.Error, ReadDrill(r.DrillDetail))).ToList();
    }

    private static readonly JsonSerializerOptions DrillJson = new(JsonSerializerDefaults.Web);

    /// <summary>The stored drill detail, or null when there is none or it cannot be read (a run's history must always list).</summary>
    private static RestoreDrillSummary? ReadDrill(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RestoreDrillSummary>(json, DrillJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One backup cycle: verified local snapshot, optional restore drill, then an encrypted off-site copy of the
/// snapshot AND the raw provider-payload archive via restic (Backblaze B2 / S3 / sftp / a folder; whatever
/// <c>Backup:Restic:Repository</c> names). Nothing in the raw archive is ever deleted. Local snapshot
/// pruning only happens after a confirmed off-site copy and never touches <c>premigrate-*</c> snapshots.
/// Every run, success or failure, is recorded in <c>backup_runs</c> so the UI can show it.
/// </summary>
public sealed partial class BackupRunner
{
    private readonly string _connectionString;
    private readonly BackupSettings _settings;
    private readonly BackupRunStore _store;
    private readonly ILogger _logger;
    private readonly BackupFailureNotifier? _notifier;

    public BackupRunner(string connectionString, BackupSettings settings, BackupRunStore store, ILogger logger, BackupFailureNotifier? notifier = null)
    {
        _notifier = notifier;
        _connectionString = connectionString;
        _settings = settings;
        _store = store;
        _logger = logger;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <param name="allowPrune">Only the scheduled job passes true; on-demand runs never delete anything.</param>
    public async Task<BackupRunResult> RunAsync(CancellationToken ct = default, bool allowPrune = false)
    {
        var started = DateTimeOffset.UtcNow;
        if (!await Gate.WaitAsync(0, ct))
        {
            return new BackupRunResult(false, started, started, null, 0, null, null, null, "A backup is already running.");
        }

        try
        {
            return await RunCoreAsync(started, allowPrune, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<BackupRunResult> RunCoreAsync(DateTimeOffset started, bool allowPrune, CancellationToken ct)
    {
        string? dir = null;
        long rows = 0;
        bool? offsiteOk = null;
        string? offsiteDetail = null;
        bool? drillOk = null;
        RestoreDrillSummary? drillSummary = null;
        string? error = null;

        try
        {
            var (snapDir, manifest) = await new DatabaseSnapshotService(_connectionString)
                .CreateAsync(_settings.BackupRoot, "scheduled backup", "backup", ct);
            dir = snapDir;
            rows = manifest.Tables.Sum(t => t.Rows);

            if (_settings.DrillAfterBackup)
            {
                var drill = await SnapshotRestoreDrill.RunAsync(_connectionString, snapDir, "flowledger_drill_" + Guid.NewGuid().ToString("N")[..8], keep: false, ct: ct, rawArchiveDir: _settings.ObjectsDir);
                drillOk = drill.Ok;
                drillSummary = new RestoreDrillSummary(drill.Tables, drill.RawArchiveChecked, drill.RawObjectsVerified, drill.RawObjectsSealed, drill.Problems);
                if (!drill.Ok)
                {
                    error = "Restore drill failed: " + string.Join("; ", drill.Problems);
                }
            }

            if (_settings.OffsiteConfigured)
            {
                (offsiteOk, offsiteDetail) = await RunResticAsync(snapDir, ct);
                if (offsiteOk != true)
                {
                    error ??= "Off-site backup failed: " + offsiteDetail;
                }
            }
            else
            {
                offsiteDetail = "No off-site repository configured (Backup:Restic:Repository).";
            }

            // Expiry (#189) runs only from the scheduled job, only after a clean run, and with an off-site repository only once that copy is confirmed.
            if (allowPrune && error is null && (offsiteOk == true || !_settings.OffsiteConfigured))
            {
                Prune();
                if (offsiteOk == true)
                {
                    offsiteDetail = await ExpireOffsiteAsync(offsiteDetail, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            LogBackupRunFailed(_logger, ex);
        }

        var result = new BackupRunResult(error is null, started, DateTimeOffset.UtcNow, dir, rows, offsiteOk, offsiteDetail, drillOk, error, drillSummary);
        try
        {
            await _store.AddAsync(result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogCouldNotRecordBackupRun(_logger, ex);
        }

        await NotifyAsync(result);
        return result;
    }

    private async Task NotifyAsync(BackupRunResult result)
    {
        try
        {
            if (_notifier is not null)
            {
                await _notifier.NotifyAsync(result, CancellationToken.None);
            }
            else
            {
                FlowLedger.Infrastructure.Observability.FlowLedgerTelemetry.RecordBackup(result.Ok, result.Ok ? "none" : BackupFailureNotifier.StageOf(result));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCouldNotNotifyBackupResult(_logger, ex);
        }
    }

    private async Task<(bool Ok, string Detail)> RunResticAsync(string snapshotDir, CancellationToken ct)
    {
        // restic reads the repository + B2/S3 credentials from the environment (never from the repo).
        var env = new Dictionary<string, string> { ["RESTIC_REPOSITORY"] = _settings.ResticRepository! };
        if (!string.IsNullOrWhiteSpace(_settings.ResticPasswordFile))
        {
            env["RESTIC_PASSWORD_FILE"] = _settings.ResticPasswordFile!;
        }

        var (cfgCode, _) = await ExecAsync(["cat", "config"], env, ct);
        if (cfgCode != 0)
        {
            var (initCode, initOut) = await ExecAsync(["init"], env, ct);
            if (initCode != 0)
            {
                return (false, "restic init failed: " + Truncate(Redact(initOut)));
            }
        }

        var paths = new List<string> { snapshotDir };
        if (!string.IsNullOrWhiteSpace(_settings.ObjectsDir) && Directory.Exists(_settings.ObjectsDir))
        {
            paths.Add(_settings.ObjectsDir!);
        }

        var args = new List<string> { "backup", "--tag", "flowledger", "--host", "flowledger" };
        args.AddRange(paths);
        var (code, output) = await ExecAsync(args, env, ct);
        return code == 0 ? (true, "OK: " + LastLine(Redact(output))) : (false, Truncate(Redact(output)));
    }

    private async Task<(int Code, string Output)> ExecAsync(IEnumerable<string> args, Dictionary<string, string> env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_settings.ResticExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(60));
        Process? p = null;
        try
        {
            p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return (p.ExitCode, await stdout + await stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, $"Could not start '{_settings.ResticExecutable}': {ex.Message}. Install restic or set Backup:Restic:Executable.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own 60-minute timeout (not the caller cancelling): stop restic and report a normal failure.
            TryKill(p);
            return (-2, "restic timed out after 60 minutes and was stopped.");
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            throw;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static void TryKill(Process? p)
    {
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    /// <summary>
    /// Off-site expiry (#189): <c>restic forget --prune</c> with the keep-daily/weekly/monthly policy, scoped to this job's tag and host. A failure
    /// does not fail the backup (the new copy is safe) but is appended to the recorded detail and logged so a retention gap is visible.
    /// </summary>
    private async Task<string?> ExpireOffsiteAsync(string? detail, CancellationToken ct)
    {
        var args = _settings.EffectiveRetention.ResticForgetArguments();
        if (args is null)
        {
            return detail;
        }

        var env = new Dictionary<string, string> { ["RESTIC_REPOSITORY"] = _settings.ResticRepository! };
        if (!string.IsNullOrWhiteSpace(_settings.ResticPasswordFile))
        {
            env["RESTIC_PASSWORD_FILE"] = _settings.ResticPasswordFile!;
        }

        var (code, output) = await ExecAsync(args, env, ct);
        if (code == 0)
        {
            return detail + "; retention applied (restic forget --prune)";
        }

        LogOffsiteExpiryFailed(_logger, code);
        return detail + "; retention FAILED: " + Truncate(Redact(output));
    }

    /// <summary>Removes expired local snapshots (see <see cref="BackupRetention"/>). Never the raw archive.</summary>
    private void Prune()
    {
        if (!Directory.Exists(_settings.BackupRoot))
        {
            return;
        }

        static IEnumerable<SnapshotFolder> Folders(string root, string pattern) =>
            Directory.GetDirectories(root, pattern).Select(SnapshotFolder.FromDirectory);

        var doomed = _settings.EffectiveRetention.SelectLocalSnapshotsToDelete(
            Folders(_settings.BackupRoot, "backup-*"), Folders(_settings.BackupRoot, "premigrate-*"), DateTimeOffset.UtcNow);
        foreach (var d in doomed)
        {
            try
            {
                Directory.Delete(d, recursive: true);
            }
            catch (Exception ex)
            {
                LogCouldNotPruneOldSnapshot(_logger, ex, d);
            }
        }
    }

    /// <summary>Strips the repository string and any scheme://user:pass@ userinfo before text is stored, logged or returned.</summary>
    private string Redact(string text)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ResticRepository))
        {
            text = text.Replace(_settings.ResticRepository!, "<repository>", StringComparison.Ordinal);
        }

        return System.Text.RegularExpressions.Regex.Replace(text, @"([a-zA-Z][a-zA-Z0-9+.-]*://)[^/\s@]+@", "$1<redacted>@");
    }

    private static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string Truncate(string s) => s.Length <= 600 ? s.Trim() : s[^600..].Trim();

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup run failed")]
    private static partial void LogBackupRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record backup run")]
    private static partial void LogCouldNotRecordBackupRun(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the backup result notification")]
    private static partial void LogCouldNotNotifyBackupResult(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Off-site retention (restic forget --prune) failed with exit code {ExitCode}; old copies are not being expired")]
    private static partial void LogOffsiteExpiryFailed(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not prune old snapshot {Dir}")]
    private static partial void LogCouldNotPruneOldSnapshot(ILogger logger, Exception exception, string? dir);
}
