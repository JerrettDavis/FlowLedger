namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// The newest cron fire time a job has been claimed for (#187). Host-wide: scheduled jobs are not per tenant. One row per job; the
/// conditional upsert in <c>EfJobFireMarks</c> is what makes "once per fire time" hold across worker replicas and restarts. The claim is
/// a lease (<see cref="Owner"/>, <see cref="ClaimedUtc"/>) confirmed by <see cref="CompletedUtc"/>.
/// </summary>
internal sealed class JobFireMarkRecord
{
    public string Job { get; private set; } = string.Empty;
    public DateTimeOffset LastFireUtc { get; private set; }
    public string? Owner { get; private set; }
    public DateTimeOffset ClaimedUtc { get; private set; }
    public DateTimeOffset? CompletedUtc { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    private JobFireMarkRecord() { }
}
