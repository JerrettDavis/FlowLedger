using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace FlowLedger.Infrastructure.Observability;

/// <summary>
/// The single <c>FlowLedger</c> <see cref="Meter"/> shared by every host (api, worker). Registered with OpenTelemetry in
/// ServiceDefaults by name, so anything recorded here is exported when an OTLP endpoint is configured.
/// </summary>
public static class FlowLedgerTelemetry
{
    public const string MeterName = "FlowLedger";

    public static readonly Meter Meter = new(MeterName, "1.0");

    /// <summary>Scheduled/background job runs, tagged <c>job</c> and <c>outcome</c> (success | failure | vetoed).</summary>
    public static readonly Counter<long> JobRuns = Meter.CreateCounter<long>("flowledger.job.runs", "{run}", "Background job runs by outcome.");

    /// <summary>Wall-clock duration of a job run, tagged <c>job</c> and <c>outcome</c>.</summary>
    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>("flowledger.job.duration", "s", "Background job run duration.");

    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastSuccess = new();

    static FlowLedgerTelemetry()
    {
        Meter.CreateObservableGauge(
            "flowledger.job.last_success_age",
            () => LastSuccess.Select(kv => new Measurement<double>(
                (DateTimeOffset.UtcNow - kv.Value).TotalSeconds, new KeyValuePair<string, object?>("job", kv.Key))),
            "s",
            "Seconds since the job last finished successfully (absent until its first success since process start).");
    }

    public static void RecordJob(string job, bool success, TimeSpan duration, DateTimeOffset finishedUtc)
    {
        var outcome = success ? "success" : "failure";
        JobRuns.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", outcome));
        JobDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", outcome));
        if (success)
        {
            LastSuccess[job] = finishedUtc;
        }
    }

    public static void RecordJobVetoed(string job)
        => JobRuns.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", "vetoed"));
}
