namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// The tables that are deliberately NOT tenant-owned (#154): no tenant column, no query filter, reached only through
/// <see cref="HostDb"/> (an explicitly unscoped context). This is the single allow-list the tenant guard tests consult, so adding a
/// host-wide table is a conscious, reviewed edit here with a reason, never an accident of forgetting a filter.
/// </summary>
internal static class HostWideEntities
{
    public static readonly IReadOnlyDictionary<string, string> Tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["provider_api_calls"] = "provider API call accounting for the whole host (cost control)",
        ["backup_runs"] = "host backup history",
    };
}
