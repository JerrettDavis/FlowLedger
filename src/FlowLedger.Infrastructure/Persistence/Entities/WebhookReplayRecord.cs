namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A seen webhook body hash (#152, #154). Hashes and timestamps only, no payload and no tenant data. Reached only through <see cref="HostDb"/>.</summary>
internal sealed class WebhookReplayRecord
{
    public string Provider { get; private set; } = string.Empty;
    public string BodyHash { get; private set; } = string.Empty;
    public DateTimeOffset SeenUtc { get; private set; }

    private WebhookReplayRecord() { }
}
