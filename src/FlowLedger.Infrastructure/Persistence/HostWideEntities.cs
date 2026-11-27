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
        ["audit_events"] = "append-only audit trail (#193); tenant_id is informational and the rows survive tenant erasure as the record",
        ["job_runs"] = "host-wide background job run history (scheduled jobs are not per tenant)",
        ["webhook_replays"] = "webhook body hashes and timestamps only (#152), no tenant data",
        ["tenants"] = "the tenant registry itself (#169), keyed by id; erasure keeps its row as the audit record",
        ["app_users"] = "the login directory: resolves a login to its tenant, so it spans tenants by design",
        ["system_settings"] = "host-wide key/value settings (the data-environment marker)",
        ["app_user_link_codes"] = "one-time codes (hashed) that join a login to a tenant; part of the login directory",
    };
}
