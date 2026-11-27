using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Outbound;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure;

// Store, outbound-integration and backup registration groups (#108).
public static partial class DependencyInjection
{
    private const string ConnectionName = "flowledger";

    private static string HostConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionName)
        ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found.");

    /// <summary>Host-wide stores (not tenant-filtered): users, tenants, erasure, webhook replay, provider call ledger, audit log, job history.</summary>
    private static void AddHostWideStores(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<FlowLedger.Application.Features.Users.IUserDirectory>(sp =>
            new EfUserDirectory(sp.GetRequiredService<HostDb>(), TenancySettings.Legacy(configuration)));
        services.AddSingleton<FlowLedger.Application.Abstractions.IWebhookReplayStore>(sp =>
            new EfWebhookReplayStore(sp.GetRequiredService<HostDb>()));
        services.AddSingleton<FlowLedger.Application.Features.Users.ITenantRegistry>(sp =>
            new EfTenantRegistry(sp.GetRequiredService<HostDb>()));
        services.AddScoped<FlowLedger.Application.Features.Users.ITenantErasure>(sp =>
            new EfTenantErasure(
                sp.GetRequiredService<FlowLedgerDbContext>(),
                TenancySettings.Legacy(configuration).LegacyTenantId,
                sp.GetServices<FlowLedger.Application.Features.Users.ITenantFileEraser>()));

        // Provider call ledger (append-only; feeds budgets + the usage page).
        services.AddSingleton<ProviderCallLedger>(sp => new ProviderCallLedger(sp.GetRequiredService<HostDb>()));
        services.AddSingleton<FlowLedger.Integrations.Abstractions.IProviderCallLedger>(sp => sp.GetRequiredService<ProviderCallLedger>());
        // Audit log of sensitive actions (append-only, host-wide EF table).
        services.AddSingleton<IAuditLog>(sp => new EfAuditLog(sp.GetRequiredService<HostDb>()));
        // Durable background job run history (host-wide EF table; worker writes, API reads).
        services.AddSingleton<IJobRunStore>(sp => new EfJobRunStore(sp.GetRequiredService<HostDb>()));
        // Fire-time claims so a missed or duplicated cron slot is detected across replicas and restarts (#187).
        services.AddSingleton<IJobFireMarks>(sp => new EfJobFireMarks(sp.GetRequiredService<HostDb>(), sp.GetService<TimeProvider>()));
    }

    /// <summary>Per-tenant stores (EF: tenant filter + guard).</summary>
    private static void AddTenantStores(IServiceCollection services)
    {
        services.AddScoped<FlowLedger.Application.Features.Categories.ICategoryMaintenanceStore, EfCategoryMaintenanceStore>();
        services.AddScoped<FlowLedger.Application.Features.Categories.ICategoryRuleStore, EfCategoryRuleStore>();
        services.AddScoped<FlowLedger.Application.Features.Transactions.IMerchantAliasStore, EfMerchantAliasStore>();
        services.AddScoped<FlowLedger.Application.Features.Dashboard.ISuspiciousDismissalStore, EfSuspiciousDismissalStore>();
        services.AddScoped<FlowLedger.Application.Features.RecurringFlows.IRecurringDismissalStore, EfRecurringDismissalStore>();
        services.AddScoped<FlowLedger.Application.Features.Budgets.IBudgetStore, EfBudgetStore>();
        services.AddScoped<FlowLedger.Application.Features.Goals.IGoalStore, EfGoalStore>();
        // Account rates: per-tenant, ad-hoc table; part of every snapshot/export.
        services.AddScoped<FlowLedger.Application.Features.Strategies.IAccountRateStore, EfAccountRateStore>();
    }

    /// <summary>Home Assistant sharing and the REST / SOAP / signed-webhook integrations (encrypted secrets, delivery log).</summary>
    private static void AddOutboundIntegrations(IServiceCollection services, IConfiguration configuration)
    {
        // Home Assistant: per-tenant settings + sent ledger; alerts only by default.
        services.AddScoped<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantStore>(sp =>
            new EfHomeAssistantStore(
                sp.GetRequiredService<FlowLedgerDbContext>(),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>(),
                ring: SecretsKeyRing(configuration, "HomeAssistant:EncryptionKey")));
        services.AddHttpClient(HomeAssistantWebhookSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(HomeAssistantWebhookSender.CreateHandler)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantNetworkPolicy, HouseholdHomeAssistantNetworkPolicy>();
        services.AddSingleton<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantSender, HomeAssistantWebhookSender>();

        // Integrations: per-tenant definitions, encrypted secrets, delivery log.
        services.AddScoped<FlowLedger.Application.Features.Integrations.IIntegrationStore>(sp =>
            new EfIntegrationStore(
                sp.GetRequiredService<FlowLedgerDbContext>(),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>(),
                ring: SecretsKeyRing(configuration, "Integrations:EncryptionKey", "HomeAssistant:EncryptionKey")));
        foreach (var clientName in new[] { IntegrationTransport.HttpClientName, IntegrationTransport.LocalHttpClientName })
        {
            services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(HomeAssistantWebhookSender.CreateHandler)
                .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));
        }

        services.AddSingleton<FlowLedger.Application.Features.Integrations.IIntegrationTransport, IntegrationTransport>();
    }

    /// <summary>Scheduled backups (verified snapshot + restore drill + restic off-site) and data retention.</summary>
    private static void AddBackups(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => BackupSettings.From(configuration));
        services.AddSingleton(_ => new BackupRunStore(HostConnectionString(configuration)));
        services.AddSingleton(sp => new BackupRunner(
            configuration.GetConnectionString(ConnectionName)!,
            sp.GetRequiredService<BackupSettings>(),
            sp.GetRequiredService<BackupRunStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Backup")));
        services.AddSingleton(_ => new DataRetention(HostConnectionString(configuration), RetentionSettings.From(configuration)));
    }
}
