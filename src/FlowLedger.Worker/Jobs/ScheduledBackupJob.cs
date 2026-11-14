using FlowLedger.Infrastructure.DataSafety;
using Quartz;

namespace FlowLedger.Worker.Jobs;

/// <summary>Daily verified backup: snapshot, restore drill, encrypted off-site copy. Failures are recorded and visible in the UI.</summary>
[DisallowConcurrentExecution]
public sealed class ScheduledBackupJob : IJob
{
    private readonly BackupRunner _runner;
    private readonly ILogger<ScheduledBackupJob> _logger;

    public ScheduledBackupJob(BackupRunner runner, ILogger<ScheduledBackupJob> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(cancellationToken);
        if (result.Ok)
        {
            _logger.LogInformation("Backup OK: {Dir} ({Rows} rows), off-site: {Offsite}", result.SnapshotDir, result.Rows, result.OffsiteDetail);
        }
        else
        {
            _logger.LogError("BACKUP FAILED: {Error}", result.Error);
        }
    }
}
