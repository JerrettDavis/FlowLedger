namespace FlowLedger.Worker.Jobs;

/// <summary>
/// Configures the Quartz-scheduled sync job.
/// Bind from the "ScheduledSync" configuration section.
/// </summary>
public sealed class JobScheduleOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ScheduledSync";

    /// <summary>
    /// When <c>false</c> the job is registered but never triggered.
    /// Allows safe disabling in environments that should not poll (e.g. PR review, staging).
    /// Default: <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cron expression controlling how often the sync runs.
    /// Default: once a day (12:00 UTC). While the web app is open it syncs hourly itself, so this
    /// background job is only the "nobody is looking" safety net — and the sync guard's minimum
    /// interval makes it a no-op if the app has synced recently.
    /// Override in appsettings.{Environment}.json or environment variables.
    /// </summary>
    public string CronExpression { get; set; } = "0 0 12 * * ?";
}
