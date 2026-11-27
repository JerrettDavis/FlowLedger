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

    /// <summary>Outbound financial-provider API calls, tagged <c>provider</c> and <c>status</c> (HTTP status class, or "error").</summary>
    public static readonly Counter<long> ProviderCalls = Meter.CreateCounter<long>("flowledger.provider.calls", "{call}", "Provider API calls by outcome.");

    public static void RecordProviderCall(string provider, int? statusCode, bool failed)
        => ProviderCalls.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("status", failed || statusCode is null ? "error" : $"{statusCode / 100}xx"));

    /// <summary>Sync runs, tagged <c>outcome</c> (success | failure | throttled | already_running).</summary>
    public static readonly Counter<long> SyncRuns = Meter.CreateCounter<long>("flowledger.sync.runs", "{run}", "Financial syncs by outcome.");

    /// <summary>Wall-clock duration of a sync that actually ran (success or failure), in seconds.</summary>
    public static readonly Histogram<double> SyncDuration = Meter.CreateHistogram<double>("flowledger.sync.duration", "s", "Financial sync duration.");

    /// <summary>Outbox deliveries, tagged <c>outcome</c> (delivered | retried | dead_lettered).</summary>
    public static readonly Counter<long> OutboxMessages = Meter.CreateCounter<long>("flowledger.outbox.messages", "{message}", "Outbox messages by delivery outcome.");

    /// <summary>Inbound provider webhooks, tagged <c>provider</c> and <c>outcome</c> (accepted | duplicate | invalid_signature | too_large | malformed | ignored).</summary>
    public static readonly Counter<long> WebhooksReceived = Meter.CreateCounter<long>("flowledger.webhook.received", "{webhook}", "Inbound webhooks by outcome.");

    /// <summary>Audit events written, tagged <c>action</c>.</summary>
    public static readonly Counter<long> AuditEvents = Meter.CreateCounter<long>("flowledger.audit.events", "{event}", "Audit events recorded by action.");

    public static void RecordSync(string outcome, TimeSpan? duration = null)
    {
        SyncRuns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (duration is { } d)
        {
            SyncDuration.Record(d.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public static void RecordOutbox(string outcome)
        => OutboxMessages.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordWebhook(string provider, string outcome)
        => WebhooksReceived.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordAudit(string action)
        => AuditEvents.Add(1, new KeyValuePair<string, object?>("action", action));

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

    /// <summary>Backup cycles (#61), tagged <c>outcome</c> (success | failure) and, on failure, <c>stage</c> (snapshot | drill | offsite).</summary>
    public static readonly Counter<long> BackupRuns = Meter.CreateCounter<long>(
        "flowledger.backup.runs", "{run}", "Backup cycles by outcome; alert on any outcome=failure.");

    public static void RecordBackup(bool ok, string stage)
        => BackupRuns.Add(1, new KeyValuePair<string, object?>("outcome", ok ? "success" : "failure"), new KeyValuePair<string, object?>("stage", stage));

    /// <summary>Automatic retries scheduled after a failed job run, tagged <c>job</c>.</summary>
    public static readonly Counter<long> JobRetries = Meter.CreateCounter<long>("flowledger.job.retries", "{retry}", "Retries scheduled for failed background jobs.");

    public static void RecordJobRetry(string job)
        => JobRetries.Add(1, new KeyValuePair<string, object?>("job", job));

    /// <summary>Fire-time gate decisions (#187), tagged <c>job</c> and <c>outcome</c> (claimed | duplicate | store_failed_skipped | store_failed_ran | complete_failed).</summary>
    public static readonly Counter<long> JobFireGate = Meter.CreateCounter<long>(
        "flowledger.job.firegate", "{decision}", "Once-per-fire-time gate decisions by job and outcome; store_failed_* means the marks table was unreachable.");

    public static void RecordJobFireGate(string job, string outcome)
        => JobFireGate.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordJobVetoed(string job)
        => JobRuns.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", "vetoed"));
}
