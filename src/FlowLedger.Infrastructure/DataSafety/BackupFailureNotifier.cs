using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Tells the operator when a backup cycle fails: always a <c>flowledger.backup.runs</c> metric and a <c>backup.failed</c> audit
/// event, plus an optional Home Assistant webhook when <c>Backup:Alert:WebhookUrl</c> is set (<c>Backup:Alert:AllowLocalNetwork</c>
/// permits a LAN address). The webhook URL is never logged.
/// </summary>
public sealed partial class BackupFailureNotifier
{
    public const string AuditAction = "backup.failed";

    private readonly IAuditLog? _audit;
    private readonly IHomeAssistantSender? _sender;
    private readonly string? _webhookUrl;
    private readonly bool _allowLocal;
    private readonly ILogger _logger;

    public BackupFailureNotifier(IConfiguration configuration, ILogger logger, IAuditLog? audit = null, IHomeAssistantSender? sender = null)
    {
        _audit = audit;
        _sender = sender;
        _logger = logger;
        _webhookUrl = configuration["Backup:Alert:WebhookUrl"];
        _allowLocal = bool.TryParse(configuration["Backup:Alert:AllowLocalNetwork"], out var a) && a;
    }

    /// <summary>Failed stage of a run: <c>drill</c>, <c>offsite</c> or <c>snapshot</c> (the error text is produced by <see cref="BackupRunner"/>).</summary>
    public static string StageOf(BackupRunResult r) =>
        r.Error is { } e && e.StartsWith("Restore drill", StringComparison.Ordinal) ? "drill"
        : r.Error is { } o && o.StartsWith("Off-site", StringComparison.Ordinal) ? "offsite"
        : "snapshot";

    public async Task NotifyAsync(BackupRunResult result, CancellationToken ct = default)
    {
        var stage = result.Ok ? "none" : StageOf(result);
        FlowLedgerTelemetry.RecordBackup(result.Ok, stage);
        if (result.Ok)
        {
            return;
        }

        if (_audit is not null)
        {
            try
            {
                await _audit.AppendAsync(
                    new AuditEvent(AuditAction, "system", null, stage, null, null, null, result.FinishedUtc), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAuditFailed(_logger, ex);
            }
        }

        if (_sender is not null && !string.IsNullOrWhiteSpace(_webhookUrl))
        {
            var failure = await _sender.SendAsync(
                _webhookUrl,
                new { @event = "flowledger.backup.failed", stage, finishedUtc = result.FinishedUtc, message = "A FlowLedger backup failed. Open Data & Backups." },
                _allowLocal,
                ct);
            if (failure is not null)
            {
                LogAlertNotDelivered(_logger, failure);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the backup.failed audit event")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup failure alert was not delivered: {Reason}")]
    private static partial void LogAlertNotDelivered(ILogger logger, string reason);
}
