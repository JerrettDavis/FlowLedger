namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A recurring-flow suggestion the tenant dismissed (#87: formerly an ad-hoc Npgsql table).</summary>
internal sealed class RecurringDismissalRecord
{
    public Guid TenantId { get; private set; }
    public string SuggestionKey { get; private set; } = string.Empty;
    public DateTimeOffset DismissedUtc { get; private set; }

    private RecurringDismissalRecord() { }
}
