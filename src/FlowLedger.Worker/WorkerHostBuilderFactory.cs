using FlowLedger.Application;
using FlowLedger.Infrastructure;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Quartz;

namespace FlowLedger.Worker;

/// <summary>
/// Centralises worker host configuration so both <c>Program.cs</c> and the test suite
/// can build a fully wired host without duplicating DI setup.
/// </summary>
public static class WorkerHostBuilderFactory
{
    /// <summary>
    /// Applies all service registrations to <paramref name="builder"/>.
    /// Called from <c>Program.cs</c> and may also be called from tests.
    /// </summary>
    public static void Configure(IHostApplicationBuilder builder)
    {
        // ── Service defaults (OpenTelemetry, health checks, service discovery) ─
        builder.AddServiceDefaults();

        // ── Application + Infrastructure (identical to API) ───────────────────
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddDataEnvironmentGuard();

        // ── Tenant (background jobs have no request to resolve one from) ──────
        // TryAdd: tests and future multi-tenant fan-out can register their own first/after.
        var workerTenant = Guid.TryParse(builder.Configuration["Worker:TenantId"], out var configuredTenant)
            ? configuredTenant
            : ConfiguredTenantContext.DemoTenantId;
        builder.Services.AddScoped(_ => new ConfiguredTenantContext(workerTenant));
        builder.Services.TryAddScoped<FlowLedger.SharedKernel.ITenantContext>(sp => sp.GetRequiredService<ConfiguredTenantContext>());

        // ── Job schedule options ──────────────────────────────────────────────
        builder.Services.Configure<JobScheduleOptions>(
            builder.Configuration.GetSection(JobScheduleOptions.SectionName));

        // ── Quartz ───────────────────────────────────────────────────────────
        builder.Services.AddQuartz(q =>
        {
            // Build-time resolution of options so the trigger can reference the cron expression.
            // At compose time the options may not yet be validated, so we read the raw config.
            var schedSection = builder.Configuration.GetSection(JobScheduleOptions.SectionName);
            var enabled = schedSection.GetValue("Enabled", defaultValue: true);
            var cron = schedSection.GetValue("CronExpression", defaultValue: "0 0 12 * * ?")!;

            var jobKey = new JobKey(nameof(ScheduledSyncJob), "FlowLedger");
            q.AddJob<ScheduledSyncJob>(opts => opts.WithIdentity(jobKey).StoreDurably());

            if (enabled)
            {
                q.AddTrigger(opts => opts
                    .ForJob(jobKey)
                    .WithIdentity($"{nameof(ScheduledSyncJob)}-trigger", "FlowLedger")
                    .WithCronSchedule(cron));
            }

            // Daily verified backup (+ restore drill + off-site copy). Default 03:15 local; Backup:Enabled=false disables.
            var backupEnabled = builder.Configuration.GetValue("Backup:Enabled", defaultValue: true);
            var backupCron = builder.Configuration.GetValue("Backup:CronExpression", defaultValue: "0 15 3 * * ?")!;
            var backupKey = new JobKey(nameof(ScheduledBackupJob), "FlowLedger");
            q.AddJob<ScheduledBackupJob>(opts => opts.WithIdentity(backupKey).StoreDurably());
            if (backupEnabled)
            {
                q.AddTrigger(opts => opts
                    .ForJob(backupKey)
                    .WithIdentity($"{nameof(ScheduledBackupJob)}-trigger", "FlowLedger")
                    .WithCronSchedule(backupCron));
            }

            // Monthly home/real-estate revaluation from the configured appraisal feed (no-op when no feed is enabled).
            var valEnabled = builder.Configuration.GetValue("AssetValuation:AutoRefresh:Enabled", defaultValue: true);
            var valCron = builder.Configuration.GetValue("AssetValuation:AutoRefresh:CronExpression", defaultValue: "0 0 4 1 * ?")!;
            var valKey = new JobKey(nameof(ScheduledAssetValuationJob), "FlowLedger");
            q.AddJob<ScheduledAssetValuationJob>(opts => opts.WithIdentity(valKey).StoreDurably());
            if (valEnabled)
            {
                q.AddTrigger(opts => opts
                    .ForJob(valKey)
                    .WithIdentity($"{nameof(ScheduledAssetValuationJob)}-trigger", "FlowLedger")
                    .WithCronSchedule(valCron));
            }
        });

        builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
    }

    /// <summary>
    /// Creates a fully configured <see cref="IHost"/> for integration/DI tests.
    /// Callers are responsible for disposing the returned host.
    /// </summary>
    public static IHost BuildTestHost(Action<IHostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // Suppress the Aspire connection-string requirement during tests.
            EnvironmentName = "Testing",
        });

        configure?.Invoke(builder);

        Configure(builder);

        return builder.Build();
    }
}
