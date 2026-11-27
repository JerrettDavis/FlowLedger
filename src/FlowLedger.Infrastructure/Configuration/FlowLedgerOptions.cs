using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Configuration;

/// <summary>Typed view of <c>Backup:*</c>. Validated at startup so a typo fails the host instead of silently changing behaviour.</summary>
internal sealed class BackupOptions
{
    public const string SectionName = "Backup";

    public bool Enabled { get; set; } = true;
    public string? CronExpression { get; set; }
    public string? Directory { get; set; }
    public int KeepLocalSnapshots { get; set; } = 30;
    public bool DrillAfterBackup { get; set; } = true;
    public bool PreMigrationSnapshot { get; set; } = true;
    public bool RequireOffsite { get; set; }
    public ResticOptions Restic { get; set; } = new();

    /// <summary>Backup expiry policy (#189); see <see cref="FlowLedger.Infrastructure.DataSafety.BackupRetention"/>.</summary>
    public RetentionOptions Retention { get; set; } = new();

    public sealed class RetentionOptions
    {
        /// <summary>Overrides <c>Backup:KeepLocalSnapshots</c> when set.</summary>
        public int? KeepLocalSnapshots { get; set; }
        public int PremigrateMaxAgeDays { get; set; } = 90;
        public bool ResticForget { get; set; } = true;
        public int ResticKeepDaily { get; set; } = 7;
        public int ResticKeepWeekly { get; set; } = 4;
        public int ResticKeepMonthly { get; set; } = 6;
    }

    public sealed class ResticOptions
    {
        public string? Repository { get; set; }
        public string? PasswordFile { get; set; }
        public string? Executable { get; set; }
    }
}

/// <summary>Typed view of <c>Sync:*</c>.</summary>
internal sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Minimum gap between syncs per tenant; absent means the provider default (30 for real providers, 0 for simulated).</summary>
    public int? MinIntervalMinutes { get; set; }
}

/// <summary>Typed view of <c>HomeAssistant:*</c> (alert push settings; the webhook URL itself is stored encrypted in the database).</summary>
internal sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    public bool Enabled { get; set; } = true;
    public string? CronExpression { get; set; }

    /// <summary>Base64 of 32 random bytes (<c>openssl rand -base64 32</c>). Optional, but without it the webhook URL cannot be stored.</summary>
    public string? EncryptionKey { get; set; }
}

internal sealed class BackupOptionsValidator : IValidateOptions<BackupOptions>
{
    public ValidateOptionsResult Validate(string? name, BackupOptions o)
    {
        var errors = new List<string>();
        if (o.KeepLocalSnapshots < 1)
        {
            errors.Add($"Backup:KeepLocalSnapshots must be a positive integer but is {o.KeepLocalSnapshots}.");
        }

        var r = o.Retention;
        if (r.KeepLocalSnapshots is < 1)
        {
            errors.Add($"Backup:Retention:KeepLocalSnapshots must be a positive integer but is {r.KeepLocalSnapshots}.");
        }

        foreach (var (key, value) in new[]
        {
            ("PremigrateMaxAgeDays", r.PremigrateMaxAgeDays), ("ResticKeepDaily", r.ResticKeepDaily),
            ("ResticKeepWeekly", r.ResticKeepWeekly), ("ResticKeepMonthly", r.ResticKeepMonthly),
        })
        {
            if (value < 0)
            {
                errors.Add($"Backup:Retention:{key} must be zero or a positive integer but is {value}.");
            }
        }

        if (r.ResticForget && r.ResticKeepDaily == 0 && r.ResticKeepWeekly == 0 && r.ResticKeepMonthly == 0)
        {
            errors.Add("Backup:Retention:ResticForget is on but ResticKeepDaily, ResticKeepWeekly and ResticKeepMonthly are all 0: restic refuses a forget with no keep rule. Set one, or set ResticForget=false.");
        }

        if (o.Directory is { } dir && dir.Length > 0 && string.IsNullOrWhiteSpace(dir))
        {
            errors.Add("Backup:Directory is whitespace; set a path or leave it unset.");
        }

        if (o.CronExpression is { } cron && cron.Length > 0 && string.IsNullOrWhiteSpace(cron))
        {
            errors.Add("Backup:CronExpression is whitespace; set a Quartz cron expression or leave it unset.");
        }

        if (o.RequireOffsite && string.IsNullOrWhiteSpace(o.Restic.Repository))
        {
            errors.Add("Backup:RequireOffsite is true but Backup:Restic:Repository is not set: backups would stay on this host only.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncOptions o)
        => o.MinIntervalMinutes is < 0 or > 10080
            ? ValidateOptionsResult.Fail($"Sync:MinIntervalMinutes must be between 0 and 10080 but is {o.MinIntervalMinutes}.")
            : ValidateOptionsResult.Success;
}

internal sealed class HomeAssistantOptionsValidator : IValidateOptions<HomeAssistantOptions>
{
    public ValidateOptionsResult Validate(string? name, HomeAssistantOptions o)
    {
        var errors = new List<string>();
        if (o.CronExpression is { } cron && cron.Length > 0 && string.IsNullOrWhiteSpace(cron))
        {
            errors.Add("HomeAssistant:CronExpression is whitespace; set a Quartz cron expression or leave it unset.");
        }

        if (!string.IsNullOrWhiteSpace(o.EncryptionKey))
        {
            try
            {
                if (Convert.FromBase64String(o.EncryptionKey.Trim()).Length != 32)
                {
                    errors.Add("HomeAssistant:EncryptionKey must be 32 bytes, base64 encoded (openssl rand -base64 32).");
                }
            }
            catch (FormatException)
            {
                errors.Add("HomeAssistant:EncryptionKey is not valid base64 (expected 32 random bytes: openssl rand -base64 32).");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public static class FlowLedgerOptionsExtensions
{
    /// <summary>Binds and validates Backup, Sync and HomeAssistant options; <c>ValidateOnStart</c> makes the host refuse to boot on bad values.</summary>
    public static IServiceCollection AddFlowLedgerTypedOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BackupOptions>().Bind(configuration.GetSection(BackupOptions.SectionName)).ValidateOnStart();
        services.AddOptions<SyncOptions>().Bind(configuration.GetSection(SyncOptions.SectionName)).ValidateOnStart();
        services.AddOptions<HomeAssistantOptions>().Bind(configuration.GetSection(HomeAssistantOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HomeAssistantOptions>, HomeAssistantOptionsValidator>());
        return services;
    }
}
