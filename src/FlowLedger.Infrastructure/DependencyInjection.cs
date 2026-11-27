using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Events;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Storage;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.Integrations.Quiltt;
using FlowLedger.Integrations.SimpleFin;
using FlowLedger.Integrations.Simulated;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure;

/// <summary>
/// Infrastructure service registration extension. Called from the API and Worker hosts.
/// </summary>
public static partial class DependencyInjection
{
    /// <summary>
    /// Registers EF Core, PostgreSQL, repositories, provider wiring, and sync service.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">Application configuration (used for connection strings).</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddCategorizationAndLocalAi(services, configuration);
        AddPersistence(services, configuration);
        AddConnectors(services, configuration);
        AddQueryTranslator(services, configuration);
        AddAssetFeeds(services, configuration);
        AddSyncPipeline(services, configuration);
        AddHostWideStores(services, configuration);
        AddTenantStores(services);
        AddOutboundIntegrations(services, configuration);
        AddBackups(services, configuration);
        AddStorageAndHealth(services, configuration);
        services.AddPostgresVersionGuard();

        return services;
    }

    /// <summary>ML categorizer tuning and the in-process local AI stack.</summary>
    private static void AddCategorizationAndLocalAi(IServiceCollection services, IConfiguration configuration)
    {
        // ML categorizer tuning ("Categorization:MlThreshold" / ":SuggestionFloor"); defaults keep auto-apply off.
        services.AddOptions<FlowLedger.Application.Features.Categories.CategorizationOptions>()
            .BindConfiguration(FlowLedger.Application.Features.Categories.CategorizationOptions.SectionName)
            .Validate(o => double.IsFinite(o.MlThreshold) && double.IsFinite(o.SuggestionFloor), "Categorization thresholds must be finite numbers.")
            .ValidateOnStart();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<FlowLedger.Application.Features.Categories.CategorizationOptions>(services);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FlowLedger.Application.Features.Categories.CategorizationOptions>>().Value);

        // ── Local AI (in-process embeddings; no external server) ─────────────────
        services.AddOptions<FlowLedger.Application.Features.LocalAi.LocalAiOptions>()
            .BindConfiguration(FlowLedger.Application.Features.LocalAi.LocalAiOptions.SectionName)
            .Validate(o => o.Neighbours > 0 && o.MaxTextsPerRequest > 0 && o.DownloadInactivityTimeout > TimeSpan.Zero,
                "LocalAi:Neighbours, MaxTextsPerRequest and DownloadInactivityTimeout must be positive.")
            .ValidateOnStart();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<FlowLedger.Application.Features.LocalAi.LocalAiOptions>(services);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FlowLedger.Application.Features.LocalAi.LocalAiOptions>>().Value);
        // No auto-redirect + connect-time IP guard (public addresses only): LocalModelManager validates every redirect hop itself.
        services.AddHttpClient(FlowLedger.Infrastructure.Ai.Local.LocalModelManager.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender.CreateHandler);
        services.AddSingleton<FlowLedger.Infrastructure.Ai.Local.LocalModelManager>();
        services.AddSingleton<FlowLedger.Application.Abstractions.ILocalAiModels>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.LocalModelManager>());
        services.AddSingleton<FlowLedger.Infrastructure.Ai.Local.LlamaSharpLocalLlm>();
        services.AddSingleton<FlowLedger.Application.Abstractions.ILocalLlm>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.LlamaSharpLocalLlm>());
        services.AddSingleton<FlowLedger.Infrastructure.Ai.Local.SwitchingTextEmbedder>();
        services.AddSingleton<FlowLedger.Application.Abstractions.ITextEmbedder>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.SwitchingTextEmbedder>());
        services.AddSingleton<FlowLedger.Application.Abstractions.ILocalAiRuntime>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.SwitchingTextEmbedder>());
    }

    /// <summary>EF Core context, domain-event dispatch, repositories and configuration (#108).</summary>
    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        // ── Database ────────────────────────────────────────────────────────────
        // The Aspire Postgres resource is named "flowledger" in AppHost.cs.
        // Aspire injects the connection string under the key "ConnectionStrings:flowledger".
        // One bounded, named NpgsqlDataSource per host: the request contexts, the host-wide stores and the singletons all draw from the same
        // pool (Database:MaxPoolSize, timeouts, Application Name), so connection use no longer scales with the number of stores (#180).
        services.AddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException(
                    "Required connection string 'flowledger' was not found. " +
                    "Ensure the Aspire Postgres resource is configured and the " +
                    "connection string is injected via Aspire service defaults.");
            return DatabaseDataSource.Create(connectionString, DatabaseConnectionOptions.From(configuration));
        });

        services.AddSingleton(sp => new HostDb(sp.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        services.AddDbContext<FlowLedgerDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(serviceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>(), npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name);
                npgsql.EnableRetryOnFailure(maxRetryCount: 5);
            });

            // Enable sensitive data logging only in Development — never in Production.
#if DEBUG
            options.EnableSensitiveDataLogging(false);
#endif
            options.EnableDetailedErrors(false);
        });

        // ── Domain event dispatching ────────────────────────────────────────────
        // Events are written to the transactional outbox inside SaveChanges (#92) and delivered by OutboxProcessor
        // (run by the Worker), which resolves every IDomainEventHandler<TEvent> registration. The context no longer dispatches inline.
        services.AddScoped<IDomainEventDispatcher, NoOpDomainEventDispatcher>();
        services.AddOptions<OutboxOptions>().BindConfiguration(OutboxOptions.SectionName).ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OutboxOptions>>().Value);
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<FlowLedger.Application.Abstractions.IOutbox, EfOutbox>();
        services.AddScoped<FlowLedger.Application.Abstractions.IRowVersionGuard, EfRowVersionGuard>();

        // Auto-register all IDomainEventHandler<> implementations in this assembly.
        var infrastructureAssembly = typeof(DependencyInjection).Assembly;
        foreach (var type in infrastructureAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var iface in type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            {
                services.AddScoped(iface, type);
            }
        }

        // ── Repositories ────────────────────────────────────────────────────────
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddHttpClient<FlowLedger.Application.Features.Strategies.IMarketRateProvider, FlowLedger.Infrastructure.Strategies.FredMarketRateProvider>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("FlowLedger-RateFetcher/1.0"));
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        // SQL GROUP BY aggregation for the dashboard (#160); the handler falls back to the query engine when this is not registered.
        services.AddScoped<FlowLedger.Application.Features.Query.ITransactionAggregateStore, EfTransactionAggregateStore>();
        services.AddScoped<FlowLedger.Application.Features.Query.TransactionAggregates>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IRecurringFlowRepository, RecurringFlowRepository>();
        services.AddScoped<IPlannedOccurrenceRepository, PlannedOccurrenceRepository>();
        services.AddScoped<ISyncCursorStore, EfSyncCursorStore>();
        services.AddScoped<IProviderConnectionStore, EfProviderConnectionStore>();
        services.AddScoped<IWebhookTenantResolver, EfWebhookTenantResolver>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System); // one injectable clock for handlers and sync (#127); tests replace it
        services.AddSingleton(sp => new SyncRetryPolicy(sp.GetService<TimeProvider>()));
        services.AddScoped<SyncAttemptGate>();
        services.AddScoped<IConnectorStatusReader, FlowLedger.Infrastructure.Sync.ConnectorStatusReader>();
        services.AddScoped<FlowLedger.Application.Abstractions.ISavedReportStore, FlowLedger.Infrastructure.Reports.EfSavedReportStore>();
        services.AddScoped<FlowLedger.Application.Abstractions.IBillSheetStore, FlowLedger.Infrastructure.Reports.EfBillSheetStore>();

        // ── Configuration ───────────────────────────────────────────────────
        // Register IConfiguration so that .BindConfiguration() (used by AddOptions) can resolve
        // it from DI. ASP.NET Core hosts pre-register this; bare ServiceCollection test setups
        // do not, so we register it here idempotently.
        if (!services.Any(d => d.ServiceType == typeof(IConfiguration)))
        {
            services.AddSingleton<IConfiguration>(configuration);
        }
    }

    /// <summary>
    /// Key ring for secrets stored at rest (#156). Seals with <c>Secrets:EncryptionKey</c>, else the first configured legacy key;
    /// opens with that key plus <c>Secrets:EncryptionKeyPrevious</c> (comma separated) plus every legacy key, so rows written
    /// before the v2 envelope (or before a rotation) stay readable.
    /// </summary>
    internal static FlowLedger.Infrastructure.DataSafety.SecretKeyRing SecretsKeyRing(IConfiguration configuration, params string[] legacyKeys)
    {
        var legacy = legacyKeys.Select(k => configuration[k]).ToArray();
        var current = configuration["Secrets:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(current))
        {
            current = legacy.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        return FlowLedger.Infrastructure.DataSafety.SecretKeyRing.From(current, [configuration["Secrets:EncryptionKeyPrevious"], .. legacy]);
    }

    /// <summary>Object storage, raw payload archive and readiness checks.</summary>
    private static void AddStorageAndHealth(IServiceCollection services, IConfiguration configuration)
    {
        FlowLedger.Infrastructure.Configuration.FlowLedgerOptionsExtensions.AddFlowLedgerTypedOptions(services, configuration);

        // ── Object storage ──────────────────────────────────────────────────────
        services.AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection(ObjectStorageOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.RootPath), "ObjectStorage:RootPath must not be empty.")
            .ValidateOnStart();
        services.AddSingleton<LocalDiskObjectStorage>();
        services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<LocalDiskObjectStorage>());
        services.AddSingleton<IObjectStorageBrowser>(sp => sp.GetRequiredService<LocalDiskObjectStorage>());
        // Raw archive (#142): tenant-partitioned keys; sealing and retention are opt-in (Storage:EncryptionKey, Storage:RawArchiveRetentionDays).
        services.AddSingleton(RawArchiveSettings.From(configuration));
        services.AddSingleton<IRawPayloadArchive, ObjectStorageRawPayloadArchive>();
        services.AddSingleton(sp => new RawArchiveMaintenance(
            sp.GetRequiredService<IObjectStorage>(), sp.GetRequiredService<IObjectStorageBrowser>(), sp.GetRequiredService<RawArchiveSettings>()));
        services.AddSingleton<FlowLedger.Integrations.Abstractions.IRawPayloadDecryptor>(sp => sp.GetRequiredService<RawArchiveMaintenance>());
        services.AddSingleton<FlowLedger.Application.Features.Users.ITenantFileEraser>(sp => sp.GetRequiredService<RawArchiveMaintenance>());

        // Readiness probes (/ready): Postgres + migrations, Redis (if configured), backup freshness.
        FlowLedger.Infrastructure.Health.ReadinessHealthExtensions.AddFlowLedgerReadinessChecks(services);
    }
}
