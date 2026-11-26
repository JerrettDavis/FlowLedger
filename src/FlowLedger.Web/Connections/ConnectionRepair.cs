using FlowLedger.Web.ApiClient;
using Microsoft.JSInterop;

namespace FlowLedger.Web.Connections;

/// <summary>Identifies one connection to repair. <see cref="ConnectionId"/> is the provider's own handle (the issue's providerCode).</summary>
public sealed record ConnectionRef(string Provider, string? ConnectionId, string? Institution = null);

public enum RepairStatus { Repaired, Cancelled, Failed, NotSupported }

public sealed record RepairResult(RepairStatus Status, string? Message = null);

/// <summary>Provider-specific "reconnect" flow. Register one per provider; the UI only ever talks to <see cref="ConnectionRepairService"/>.</summary>
public interface IConnectionRepairHandler
{
    bool CanRepair(string provider);

    Task<RepairResult> RepairAsync(ConnectionRef connection, CancellationToken ct = default);
}

/// <summary>Picks the handler for a provider, or falls back to generic instructions when there is none.</summary>
public sealed class ConnectionRepairService(IEnumerable<IConnectionRepairHandler> handlers)
{
    private readonly IReadOnlyList<IConnectionRepairHandler> _handlers = handlers.ToList();

    public IConnectionRepairHandler? Find(string provider) => _handlers.FirstOrDefault(h => h.CanRepair(provider));

    public Task<RepairResult> RepairAsync(ConnectionRef connection, CancellationToken ct = default) =>
        Find(connection.Provider) is { } handler
            ? handler.RepairAsync(connection, ct)
            : Task.FromResult(new RepairResult(RepairStatus.NotSupported, FallbackMessage(connection)));

    public static string FallbackMessage(ConnectionRef connection) =>
        $"Reconnect {(string.IsNullOrWhiteSpace(connection.Institution) ? "this connection" : connection.Institution)} " +
        $"on {connection.Provider}'s own site or app, then press Sync now.";
}

/// <summary>Reconnects a Quiltt connection in place via <c>Quiltt.reconnect(connectorId, { connectionId })</c>.</summary>
public sealed class QuilttRepairHandler(FlowLedgerApiClient api, IJSRuntime js) : IConnectionRepairHandler
{
    public const string ProviderId = "Quiltt";

    public bool CanRepair(string provider) => string.Equals(provider, ProviderId, StringComparison.OrdinalIgnoreCase);

    public async Task<RepairResult> RepairAsync(ConnectionRef connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.ConnectionId))
        {
            return new RepairResult(RepairStatus.Failed, "Cannot reconnect: the connection reference is missing. Use Connect bank instead.");
        }

        var session = await api.StartQuilttSessionAsync(ct);
        if (session is null)
        {
            return new RepairResult(RepairStatus.Failed, "Could not start the bank connection.");
        }

        var result = await js.InvokeAsync<QuilttConnectResult>(
            "flowledgerQuiltt.reconnect", ct, session.ScriptUrl, session.ConnectorId, session.Token, connection.ConnectionId);
        return result.Status switch
        {
            "success" => new RepairResult(RepairStatus.Repaired),
            "cancelled" => new RepairResult(RepairStatus.Cancelled),
            _ => new RepairResult(RepairStatus.Failed, "The reconnect did not complete. Please try again."),
        };
    }
}
