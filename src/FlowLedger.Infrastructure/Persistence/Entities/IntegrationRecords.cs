namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A per-tenant integration definition (#154: formerly an ad-hoc Npgsql table). Config is JSON; secrets are stored sealed.</summary>
internal sealed class IntegrationRecord
{
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public string Config { get; private set; } = string.Empty;
    public string? Secrets { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset? LastUsedUtc { get; private set; }
    public string? LastError { get; private set; }

    private IntegrationRecord() { }
}

/// <summary>One delivery log row (#154): status and timing only, never request or response bodies.</summary>
internal sealed class IntegrationDeliveryRecord
{
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public Guid IntegrationId { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }
    public string Trigger { get; private set; } = string.Empty;
    public string Method { get; private set; } = string.Empty;
    public string Target { get; private set; } = string.Empty;
    public int? StatusCode { get; private set; }
    public int DurationMs { get; private set; }
    public string? Error { get; private set; }

    private IntegrationDeliveryRecord() { }
}
