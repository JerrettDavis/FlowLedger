using System.Diagnostics;
using FlowLedger.Infrastructure.HostStores;
using FlowLedger.SharedKernel;

namespace FlowLedger.Api.Middleware;

/// <summary>
/// Writes an <c>audit_events</c> row for every request that reaches one of the sensitive endpoints below (connector link/unlink,
/// data export/import/backup-now, integration and Home Assistant credential changes, login linking, key rotation, tenant offboarding and
/// destructive edits such as deactivating an account, merging duplicates or deleting a saved report, sheet row or rule). Keyed by endpoint name so a
/// route change cannot silently drop auditing; <c>AuditMiddlewareTests</c> pins that every name still exists.
/// A failing audit write is logged and never changes the response (the action already happened).
/// </summary>
internal sealed class AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
{
    /// <summary>Endpoint name to audit action.</summary>
    internal static readonly IReadOnlyDictionary<string, string> AuditedEndpoints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SetAccountConnector"] = "connector.link",
        ["ClearAccountConnector"] = "connector.unlink",
        ["Connect"] = "connector.connect",
        ["MxConnectToken"] = "connector.mx_connect",
        ["QuilttSession"] = "connector.quiltt_session",
        ["BackupNow"] = "data.backup_now",
        ["ExportData"] = "data.export",
        ["ImportData"] = "data.import",
        ["SaveHomeAssistantSettings"] = "home_assistant.save",
        ["CreateIntegration"] = "integration.create",
        ["UpdateIntegration"] = "integration.update",
        ["DeleteIntegration"] = "integration.delete",
        ["RotateIntegrationSecret"] = "integration.rotate_secret",
        ["CreateLinkCode"] = "user.link_code",
        ["RedeemLinkCode"] = "user.link",
        ["UnlinkLogin"] = "user.unlink",

        // Key rotation and destructive edits (#193).
        ["ReencryptSecrets"] = "secrets.reencrypt",
        ["SealRawArchive"] = "archive.seal",
        ["OffboardTenant"] = "tenant.offboard",
        ["DeactivateAccount"] = "account.deactivate",
        ["DeactivateAsset"] = "asset.deactivate",
        ["DeactivateRecurringFlow"] = "recurring.deactivate",
        ["DeleteCategory"] = "category.delete",
        ["MergeDuplicate"] = "transaction.merge_duplicate",
        ["ClearAutoCategories"] = "category.clear_auto",
        ["DeleteBudget"] = "budget.delete",
        ["DeleteGoal"] = "goal.delete",
        ["DeleteReport"] = "report.delete",
        ["DeleteSheetBill"] = "sheet.row_delete",
        ["DeleteCategoryRule"] = "category_rule.delete",
        ["DeleteMerchantAlias"] = "merchant_alias.delete",
    };

    public async Task InvokeAsync(HttpContext context, IAuditLog audit, TimeProvider clock)
    {
        var endpoint = context.GetEndpoint();
        var name = endpoint?.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName;
        if (name is null || !AuditedEndpoints.TryGetValue(name, out var action))
        {
            await next(context);
            return;
        }

        // The exception handler sits outside this middleware, so on a throw the response status is still 200 here: record 500.
        int? thrown = null;
        try
        {
            await next(context);
        }
        catch (Exception)
        {
            thrown = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            await TryRecordAsync(context, audit, clock, action, thrown);
        }
    }

    private async Task TryRecordAsync(HttpContext context, IAuditLog audit, TimeProvider clock, string action, int? statusOverride)
    {
        try
        {
            Guid? tenantId = null;
            var userId = Guid.Empty;
            try
            {
                var tenant = context.RequestServices.GetService<ITenantContext>();
                if (tenant is not null)
                {
                    tenantId = tenant.TenantId;
                    userId = tenant.UserId;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tenant could not be resolved (fails closed elsewhere); audit the attempt without one.
            }

            var principal = context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } n } ? n : "anonymous";
            var actor = userId == Guid.Empty ? principal : $"{principal}/{userId}";
            // Decoded path: strip control characters so a crafted %0d%0a cannot forge a line in anything that later prints the row.
            var target = context.Request.Path.Value is { } rawPath ? string.Concat(rawPath.Where(c => !char.IsControl(c))) : null;

            // Rules and aliases are identified by their pattern, not an id, so the trail names the one that was deleted (control characters stripped, capped).
            if (target is not null && context.Request.Query.TryGetValue("pattern", out var pattern) && pattern.ToString() is { Length: > 0 } p)
            {
                var clean = string.Concat(p.Where(c => !char.IsControl(c)));
                target += "?pattern=" + (clean.Length > 120 ? clean[..120] : clean);
            }

            await audit.AppendAsync(new AuditEvent(
                action,
                actor,
                tenantId,
                target,
                context.Connection.RemoteIpAddress?.ToString(),
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                statusOverride ?? context.Response.StatusCode,
                clock.GetUtcNow()), CancellationToken.None);
        }
        catch (Exception ex)
        {
            ApiLog.AuditNotRecorded(logger, ex, action);
        }
    }
}
