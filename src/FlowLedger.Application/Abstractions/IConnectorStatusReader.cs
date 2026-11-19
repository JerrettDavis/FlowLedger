namespace FlowLedger.Application.Abstractions;

/// <summary>How many billable provider calls were made today and this month (UTC).</summary>
public sealed record ConnectorUsage(long CallsToday, long CallsThisMonth);

/// <summary>Read-only status for the Connectors page: provider call usage and the current tenant's last successful sync.</summary>
public interface IConnectorStatusReader
{
    Task<ConnectorUsage> GetUsageAsync(string providerName, CancellationToken ct = default);

    Task<DateTimeOffset?> LastSyncedAtAsync(string providerName, CancellationToken ct = default);
}
