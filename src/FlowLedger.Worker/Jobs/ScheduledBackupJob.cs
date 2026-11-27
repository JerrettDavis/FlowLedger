using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Storage;
using Quartz;

namespace FlowLedger.Worker.Jobs;

/// <summary>Daily verified backup: snapshot, restore drill, encrypted off-site copy. Failures are recorded and visible in the UI.</summary>
[DisallowConcurrentExecution]
internal sealed class ScheduledBackupJob : IJob
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

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(cancellationToken, allowPrune: true);
        if (result.Ok)
        {
            _logger.LogInformation("Backup OK: {Dir} ({Rows} rows), off-site: {Offsite}", result.SnapshotDir, result.Rows, result.OffsiteDetail);
        }
        else
        {
            _logger.LogError("BACKUP FAILED: {Error}", result.Error);
            // Surface the failure to the job listener (history + flowledger.job.runs{outcome=failure}); Quartz does not refire on this.
            throw new JobExecutionException($"Backup failed: {result.Error}");
        }

        try
        {
            // Merge snapshots are only needed for undo; spent link codes go shortly after expiry (Retention:* configuration, defaults 90 days / 24 hours).
            var pruned = await _retention.PruneAsync(cancellationToken);
            if (pruned.SnapshotsBlanked > 0 || pruned.LinkCodesDeleted > 0)
            {
                _logger.LogInformation("Retention: blanked {Snapshots} merge snapshots, deleted {Codes} link codes.", pruned.SnapshotsBlanked, pruned.LinkCodesDeleted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Data retention pass failed; it will run again with the next backup.");
        }

        if (_rawArchive is not null)
        {
            try
            {
                // Opt-in (#142): does nothing unless Storage:RawArchiveRetentionDays is set above 0.
                var removed = await _rawArchive.PruneConfiguredAsync(DateTimeOffset.UtcNow, cancellationToken);
                if (removed > 0)
                {
                    _logger.LogInformation("Retention: removed {Files} raw provider payload files past the retention window.", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Raw archive retention pass failed; it will run again with the next backup.");
            }
        }
    }
}
