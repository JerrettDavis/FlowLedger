namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A user-entered deposit APY or loan APR for one account (#87: formerly an ad-hoc Npgsql table).</summary>
internal sealed class AccountRateRecord
{
    public Guid TenantId { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal RatePercent { get; private set; }
    public int? RemainingMonths { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    private AccountRateRecord() { }
}
