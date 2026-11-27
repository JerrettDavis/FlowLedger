using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Storage;
using Quartz;

namespace FlowLedger.Worker.Jobs;

/// <summary>Daily verified backup: snapshot, restore drill, encrypted off-site copy. Failures are recorded and visible in the UI.</summary>
[DisallowConcurrentExecution]
internal sealed partial class ScheduledBackupJob : IJob
{
    private readonly BackupRunner _runner;
    private readonly DataRetention _retention;
    private readonly ILogger<ScheduledBackupJob> _logger;
    private readonly RawArchiveMaintenance? _rawArchive;

    public ScheduledBackupJob(BackupRunner runner, DataRetention retention, ILogger<ScheduledBackupJob> logger, RawArchiveMaintenance? rawArchive = null)
    {
        _runner = runner;
        _retention = retention;
        _logger = logger;
        _rawArchive = rawArchive;
    }

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        => FlowLedger.Worker.Scheduling.JobTrace.RunAsync(context, _logger, ct => ExecuteCoreAsync(context, ct), cancellationToken);

    private async ValueTask ExecuteCoreAsync(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(cancellationToken, allowPrune: true);
        if (result.Ok)
        {
            LogBackupOk(result.SnapshotDir, result.Rows, result.OffsiteDetail);
        }
        else
        {
            LogBackupFailed(result.Error);
            // Surface the failure to the job listener (history + flowledger.job.runs{outcome=failure}); Quartz does not refire on this.
            throw new JobExecutionException($"Backup failed: {result.Error}");
        }

        try
        {
            // Merge snapshots are only needed for undo; spent link codes go shortly after expiry (Retention:* configuration, defaults 90 days / 24 hours).
            var pruned = await _retention.PruneAsync(cancellationToken);
            if (pruned.SnapshotsBlanked > 0 || pruned.LinkCodesDeleted > 0)
            {
                LogRetentionPruned(pruned.SnapshotsBlanked, pruned.LinkCodesDeleted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRetentionFailed(ex);
        }

        if (_rawArchive is not null)
        {
            try
            {
                // Opt-in (#142): does nothing unless Storage:RawArchiveRetentionDays is set above 0.
                var removed = await _rawArchive.PruneConfiguredAsync(DateTimeOffset.UtcNow, cancellationToken);
                if (removed > 0)
                {
                    LogRawArchivePruned(removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRawArchiveRetentionFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup OK: {Dir} ({Rows} rows), off-site: {Offsite}")]
    private partial void LogBackupOk(string? dir, long rows, string? offsite);

    [LoggerMessage(Level = LogLevel.Error, Message = "BACKUP FAILED: {Error}")]
    private partial void LogBackupFailed(string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: blanked {Snapshots} merge snapshots, deleted {Codes} link codes.")]
    private partial void LogRetentionPruned(int snapshots, int codes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Data retention pass failed; it will run again with the next backup.")]
    private partial void LogRetentionFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: removed {Files} raw provider payload files past the retention window.")]
    private partial void LogRawArchivePruned(int files);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raw archive retention pass failed; it will run again with the next backup.")]
    private partial void LogRawArchiveRetentionFailed(Exception exception);
}
