using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace FlowLedger.Worker;

/// <summary>
/// Startup validation for the Worker, which otherwise reads most settings through <c>GetValue</c> with silent defaults
/// (a typo'd flag or cron would quietly disable backups, or fail only when Quartz builds the trigger). Collects every
/// problem so the operator fixes them in one pass, and is run before any service is registered.
/// </summary>
public static class WorkerConfigValidator
{
    private static readonly (string Key, string Label)[] CronKeys =
    [
        ("ScheduledSync:CronExpression", "sync"),
        ("Backup:CronExpression", "backup"),
        ("AssetValuation:AutoRefresh:CronExpression", "asset valuation"),
        ("HomeAssistant:CronExpression", "Home Assistant"),
    ];

    private static readonly string[] BoolKeys =
    [
        "ScheduledSync:Enabled", "Backup:Enabled", "AssetValuation:AutoRefresh:Enabled", "HomeAssistant:Enabled",
        "Backup:RequireOffsite", "Backup:DrillAfterBackup", "Backup:PreMigrationSnapshot",
    ];

    public static IReadOnlyList<string> Validate(IConfiguration config, string environmentName)
    {
        var errors = new List<string>();
        var production = !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);

        var conn = config.GetConnectionString("flowledger");
        if (string.IsNullOrWhiteSpace(conn))
        {
            errors.Add("ConnectionStrings:flowledger is not set (env ConnectionStrings__flowledger).");
        }
        else if (production && ConfigGuards.LooksLikePlaceholder(conn))
        {
            errors.Add("ConnectionStrings:flowledger still contains a template placeholder.");
        }

        foreach (var (key, label) in CronKeys)
        {
            if (config[key] is { Length: > 0 } cron && !CronExpression.TryParse(cron, out _))
            {
                errors.Add($"{key} ('{cron}') is not a valid Quartz cron expression for the {label} job.");
            }
        }

        foreach (var key in BoolKeys)
        {
            if (config[key] is { Length: > 0 } raw && !bool.TryParse(raw, out _))
            {
                errors.Add($"{key} must be 'true' or 'false' but is '{raw}'.");
            }
        }

        if (config["Worker:TenantId"] is { Length: > 0 } tenant && !Guid.TryParse(tenant, out _))
        {
            errors.Add($"Worker:TenantId ('{tenant}') is not a valid GUID.");
        }

        if (config["Backup:KeepLocalSnapshots"] is { Length: > 0 } keep && (!int.TryParse(keep, out var n) || n < 1))
        {
            errors.Add($"Backup:KeepLocalSnapshots must be a positive integer but is '{keep}'.");
        }

        if (bool.TryParse(config["Backup:RequireOffsite"], out var requireOffsite) && requireOffsite
            && string.IsNullOrWhiteSpace(config["Backup:Restic:Repository"]))
        {
            errors.Add("Backup:RequireOffsite is true but Backup:Restic:Repository is not set: backups would stay on this host only.");
        }

        if (config["Backup:Directory"] is { Length: > 0 } dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                errors.Add($"Backup:Directory ('{dir}') cannot be created or written: {ex.Message}");
            }
        }

        if (production)
        {
            if (bool.TryParse(config["Mx:Enabled"], out var mx) && mx)
            {
                foreach (var key in new[] { "Mx:ApiKey", "Mx:ClientId", "Mx:WebhookSecret" })
                {
                    var v = config[key];
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        errors.Add($"{key} is required when Mx:Enabled is true.");
                    }
                    else if (ConfigGuards.LooksLikePlaceholder(v))
                    {
                        errors.Add($"{key} still contains a template placeholder.");
                    }
                }
            }

            foreach (var secretKey in new[] { "HomeAssistant:EncryptionKey", "Integrations:EncryptionKey", "Secrets:EncryptionKey", "Secrets:EncryptionKeyPrevious" })
            {
                if (ConfigGuards.LooksLikePlaceholder(config[secretKey]))
                {
                    errors.Add($"{secretKey} still contains a template placeholder.");
                }
            }
        }

        return errors;
    }

    /// <summary>Throws one <see cref="InvalidOperationException"/> listing every problem.</summary>
    public static void ValidateOrThrow(IConfiguration config, string environmentName)
    {
        var errors = Validate(config, environmentName);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "The FlowLedger worker configuration is invalid:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
        }
    }
}
