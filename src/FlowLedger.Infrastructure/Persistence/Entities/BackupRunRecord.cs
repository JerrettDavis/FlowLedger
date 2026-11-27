namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>One backup cycle, shown in the UI (#154). Host-wide: no tenant column, no tenant filter; reached only through <see cref="HostDb"/>.</summary>
internal sealed class BackupRunRecord
{
    public long Id { get; private set; }
    public DateTimeOffset StartedUtc { get; private set; }
    public DateTimeOffset FinishedUtc { get; private set; }
    public bool Ok { get; private set; }
    public string? SnapshotDir { get; private set; }
    public long Rows { get; private set; }
    public bool? OffsiteOk { get; private set; }
    public string? OffsiteDetail { get; private set; }
    public bool? DrillOk { get; private set; }
    public string? Error { get; private set; }

    private BackupRunRecord() { }

    public static BackupRunRecord Create(
        DateTimeOffset startedUtc, DateTimeOffset finishedUtc, bool ok, string? snapshotDir, long rows,
        bool? offsiteOk, string? offsiteDetail, bool? drillOk, string? error) =>
        new()
        {
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Ok = ok,
            SnapshotDir = snapshotDir,
            Rows = rows,
            OffsiteOk = offsiteOk,
            OffsiteDetail = offsiteDetail,
            DrillOk = drillOk,
            Error = error,
        };
}
