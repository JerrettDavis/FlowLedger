using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>The link between an account and the connector that feeds it (#154: formerly an ad-hoc Npgsql table).</summary>
internal sealed class AccountConnectorLinkRecord
{
    public Guid TenantId { get; private set; }
    public Guid AccountId { get; private set; }
    public ConnectorId ConnectorId { get; private set; }
    public string ProviderAccountId { get; private set; } = string.Empty;
    public DateTimeOffset LinkedUtc { get; private set; }

    private AccountConnectorLinkRecord() { }
}
