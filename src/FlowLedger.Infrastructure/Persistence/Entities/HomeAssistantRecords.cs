namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>Per-tenant Home Assistant sharing settings (#154: formerly an ad-hoc Npgsql table). The webhook is stored sealed.</summary>
internal sealed class HomeAssistantSettingsRecord
{
    public Guid TenantId { get; private set; }
    public bool Enabled { get; private set; }
    public string? Webhook { get; private set; }
    public bool ShareAmounts { get; private set; }
    public bool IncludeNames { get; private set; }
    public int LeadDays { get; private set; }
    public string Kinds { get; private set; } = string.Empty;
    public DateTimeOffset? LastSentUtc { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    private HomeAssistantSettingsRecord() { }
}

/// <summary>The "already sent" dedupe ledger (#154): opaque keys only, never amounts.</summary>
internal sealed class HomeAssistantSentRecord
{
    public Guid TenantId { get; private set; }
    public string DedupeKey { get; private set; } = string.Empty;
    public DateTimeOffset SentUtc { get; private set; }

    private HomeAssistantSentRecord() { }
}
