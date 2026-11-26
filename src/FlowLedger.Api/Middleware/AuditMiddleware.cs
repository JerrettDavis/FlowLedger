using System.Diagnostics;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Routing;

namespace FlowLedger.Api.Middleware;

/// <summary>
/// Writes an <c>audit_events</c> row for every request that reaches one of the sensitive endpoints below (connector link/unlink,
/// data export/import/backup-now, integration and Home Assistant credential changes, login linking). Keyed by endpoint name so a
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

        try
        {
            await next(context);
        }
        finally
        {
            await TryRecordAsync(context, audit, clock, action);
        }
    }

    private async Task TryRecordAsync(HttpContext context, IAuditLog audit, TimeProvider clock, string action)
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
            var target = context.Request.Path.Value;

            await audit.AppendAsync(new AuditEvent(
                action,
                actor,
                tenantId,
                target,
                context.Connection.RemoteIpAddress?.ToString(),
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                context.Response.StatusCode,
                clock.GetUtcNow()), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit event {Action} could not be recorded.", action);
        }
    }
}
