namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// The last cron fire time a job has been claimed for (#187). Host-wide: scheduled jobs are not per tenant. One row per job; the
/// conditional upsert in <c>EfJobFireMarks</c> is what makes "once per fire time" hold across worker replicas and restarts.
/// </summary>
internal sealed class JobFireMarkRecord
{
    public string Job { get; private set; } = string.Empty;
    public DateTimeOffset LastFireUtc { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    private JobFireMarkRecord() { }
}
