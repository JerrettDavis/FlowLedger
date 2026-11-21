using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Events;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Storage;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
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
public static class DependencyInjection
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
        // ML categorizer tuning ("Categorization:MlThreshold" / ":SuggestionFloor"); defaults keep auto-apply off.
        var categorization = new FlowLedger.Application.Features.Categories.CategorizationOptions();
        if (double.TryParse(configuration["Categorization:MlThreshold"], System.Globalization.CultureInfo.InvariantCulture, out var mlThreshold))
        {
            categorization.MlThreshold = mlThreshold;
        }

        if (double.TryParse(configuration["Categorization:SuggestionFloor"], System.Globalization.CultureInfo.InvariantCulture, out var mlFloor))
        {
            categorization.SuggestionFloor = mlFloor;
        }

        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<FlowLedger.Application.Features.Categories.CategorizationOptions>(services);
        services.AddSingleton(categorization);

        // ── Local AI (in-process embeddings; no external server) ─────────────────
        var localAi = new FlowLedger.Application.Features.LocalAi.LocalAiOptions();
        configuration.GetSection(FlowLedger.Application.Features.LocalAi.LocalAiOptions.SectionName).Bind(localAi);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<FlowLedger.Application.Features.LocalAi.LocalAiOptions>(services);
        services.AddSingleton(localAi);
        services.AddHttpClient(FlowLedger.Infrastructure.Ai.Local.LocalModelManager.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(30));
        services.AddSingleton<FlowLedger.Infrastructure.Ai.Local.LocalModelManager>();
        services.AddSingleton<FlowLedger.Application.Abstractions.ILocalAiModels>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.LocalModelManager>());
        services.AddSingleton<FlowLedger.Infrastructure.Ai.Local.LlamaSharpLocalLlm>();
        services.AddSingleton<FlowLedger.Application.Abstractions.ILocalLlm>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.LlamaSharpLocalLlm>());
        services.AddSingleton<FlowLedger.Infrastructure.Ai.Local.SwitchingTextEmbedder>();
        services.AddSingleton<FlowLedger.Application.Abstractions.ITextEmbedder>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.SwitchingTextEmbedder>());
        services.AddSingleton<FlowLedger.Application.Abstractions.ILocalAiRuntime>(sp => sp.GetRequiredService<FlowLedger.Infrastructure.Ai.Local.SwitchingTextEmbedder>());

        // ── Database ────────────────────────────────────────────────────────────
        // The Aspire Postgres resource is named "flowledger" in AppHost.cs.
        // Aspire injects the connection string under the key "ConnectionStrings:flowledger".
        services.AddDbContext<FlowLedgerDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException(
                    "Required connection string 'flowledger' was not found. " +
                    "Ensure the Aspire Postgres resource is configured and the " +
                    "connection string is injected via Aspire service defaults.");

            options.UseNpgsql(connectionString, npgsql =>
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
        // DispatchingDomainEventDispatcher resolves all IDomainEventHandler<TEvent>
        // registrations for each raised event via closed-generic reflection.
        services.AddScoped<IDomainEventDispatcher, DispatchingDomainEventDispatcher>();

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
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IRecurringFlowRepository, RecurringFlowRepository>();
        services.AddScoped<IPlannedOccurrenceRepository, PlannedOccurrenceRepository>();
        services.AddScoped<ISyncCursorStore, EfSyncCursorStore>();
        services.AddScoped<IProviderConnectionStore, EfProviderConnectionStore>();
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

        // ── Provider wiring ────────────────────────────────────────────────────
        // Provider selection is config-only:
        //   Mx:Enabled = true  → real MX provider (FlowLedger.Integrations.Mx)
        //   Mx:Enabled = false → Simulated provider (default; no API key required)
        //
        // When Enabled = true, FinancialProviderOptionsValidator fails fast at startup if any
        // required credential (ApiKey/ClientId/BaseUrl/WebhookSecret) is missing. That loud
        // failure is the intended "plug in the key and roll" behaviour — there is deliberately
        // NO silent fallback to fake data when MX is enabled.
        services.AddOptions<FinancialProviderOptions>()
            .BindConfiguration(FinancialProviderOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FinancialProviderOptions>, FinancialProviderOptionsValidator>();

        var mxOptions = configuration.GetSection(FinancialProviderOptions.SectionName).Get<FinancialProviderOptions>()
                        ?? new FinancialProviderOptions();

        // Connectors are enabled per instance by configuration; accounts choose their connector (account_connector_links, ADR 0010).
        // Several real connectors may be on together. Simulated is on when no real connector is (or explicitly: Connectors:Simulated=true,
        // which adds fake demo accounts next to real ones - only for trying things out).
        var simpleFinEnabled = bool.TryParse(configuration["SimpleFin:Enabled"], out var sf) && sf;
        var simulatedExplicit = bool.TryParse(configuration["Connectors:Simulated"], out var simOn) && simOn;
        var simulatedEnabled = (!simpleFinEnabled && !mxOptions.Enabled) || simulatedExplicit;

        // The first enabled connector (SimpleFIN, MX, Simulated) is also the un-keyed "primary" IFinancialDataProvider for older call sites.
        var connectors = new List<FlowLedger.Integrations.Abstractions.ConnectorDescriptor>();
        if (simpleFinEnabled)
        {
            services.AddSimpleFinProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(new("SimpleFIN", "SimpleFIN Bridge", "Aggregator (personal)", SharedCredentials: true));
        }

        if (mxOptions.Enabled)
        {
            services.AddMxProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(new("MX", "MX", "Aggregator (widget)", SharedCredentials: true));
        }

        if (simulatedEnabled)
        {
            services.AddSimulatedProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(new("Simulated", "Simulated demo data", "Demo", SharedCredentials: false));
        }

        services.AddScoped<FlowLedger.Application.Abstractions.IAccountConnectorLinkStore>(sp =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlAccountConnectorLinkStore(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>()));

        services.AddScoped<FlowLedger.Application.Abstractions.IProviderAccountCatalog>(sp =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlProviderAccountCatalog(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>()));

        services.AddScoped<FlowLedger.Integrations.Abstractions.IConnectorRegistry>(sp =>
            new FlowLedger.Infrastructure.Sync.ConnectorRegistry(sp, connectors));

        // Local LLM (Ollama) for "ask a question" -> query. Loopback by default; data never leaves the machine.
        var ollama = new FlowLedger.Infrastructure.Ai.OllamaOptions();
        configuration.GetSection("Ai:Ollama").Bind(ollama);
        services.AddSingleton(ollama);
        services.AddHttpClient<FlowLedger.Application.Features.Query.IQueryTranslator, FlowLedger.Infrastructure.Ai.OllamaQueryTranslator>(
            (sp, c) =>
            {
                var o = sp.GetRequiredService<FlowLedger.Infrastructure.Ai.OllamaOptions>();
                c.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            });

        // ── Asset valuation feeds (generic HTTP JSON feed; disabled unless AssetFeeds:Http:Enabled) ──
        services.Configure<FlowLedger.Infrastructure.Assets.HttpJsonAppraisalFeedOptions>(
            configuration.GetSection(FlowLedger.Infrastructure.Assets.HttpJsonAppraisalFeedOptions.SectionName));
        services.AddHttpClient(FlowLedger.Infrastructure.Assets.HttpJsonAppraisalFeed.HttpClientName,
            c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IAssetValuationFeed, FlowLedger.Infrastructure.Assets.HttpJsonAppraisalFeed>();

        // RealtyAPI.io (Redfin Estimate) home values; disabled unless RealtyApi:Enabled + ApiKey.
        services.Configure<FlowLedger.Infrastructure.Assets.RealtyApiOptions>(
            configuration.GetSection(FlowLedger.Infrastructure.Assets.RealtyApiOptions.SectionName));
        services.AddHttpClient(FlowLedger.Infrastructure.Assets.RealtyApiValuationFeed.HttpClientName,
            c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IAssetValuationFeed, FlowLedger.Infrastructure.Assets.RealtyApiValuationFeed>();

        // Tulsa County Assessor (OK) market value; public site, disabled unless TulsaAssessor:Enabled.
        services.Configure<FlowLedger.Infrastructure.Assets.TulsaAssessorOptions>(
            configuration.GetSection(FlowLedger.Infrastructure.Assets.TulsaAssessorOptions.SectionName));
        services.AddHttpClient(FlowLedger.Infrastructure.Assets.TulsaCountyAssessorValuationFeed.HttpClientName,
            c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IAssetValuationFeed, FlowLedger.Infrastructure.Assets.TulsaCountyAssessorValuationFeed>();

        services.AddSingleton(new FlowLedger.Application.Features.Assets.AssetValuationSettings
        {
            BlendWindowDays = Math.Max(0, configuration.GetValue("AssetValuation:BlendWindowDays", 7)),
        });

        // ── Sync service ────────────────────────────────────────────────────────
        services.AddScoped<FinancialSyncService>();
        services.AddScoped<FlowLedger.Application.Abstractions.IBalanceHistoryReader, FlowLedger.Infrastructure.Persistence.Repositories.BalanceHistoryReader>();
        services.AddScoped<FlowLedger.Application.Features.Categories.ICategorizationStore, FlowLedger.Infrastructure.Persistence.Repositories.CategorizationStore>();
        // Decorators (outer -> inner): bill-sheet refresh -> cost guard (min interval + single-flight) -> sync.
        services.AddScoped<FlowLedger.Infrastructure.Sync.MultiConnectorSyncService>();
        services.AddScoped<IFinancialSyncService>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var registry = sp.GetRequiredService<FlowLedger.Integrations.Abstractions.IConnectorRegistry>();
            var tenant = sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>().TenantId;
            // Operator-held connector credentials belong to the household (legacy) tenant only - never sync them into another user's tenant.
            var usable = FlowLedger.Infrastructure.Sync.MultiConnectorSyncService.UsableFor(
                registry, tenant, TenancySettings.Legacy(cfg).LegacyTenantId);
            if (usable.Count == 0)
            {
                return new FlowLedger.Infrastructure.Sync.UnlinkedTenantSyncService();
            }

            // Real connectors default to 30 min between syncs; the free Simulated provider is unthrottled.
            var defaultMinutes = usable.Any(c => c.Id != "Simulated") ? 30 : 0;
            var minutes = int.TryParse(cfg["Sync:MinIntervalMinutes"], out var m) ? m : defaultMinutes;
            var db = sp.GetRequiredService<FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext>();
            var connectorIds = usable.Select(c => c.Id).ToArray();

            var guarded = new FlowLedger.Infrastructure.Sync.GuardedSyncService(
                sp.GetRequiredService<FlowLedger.Infrastructure.Sync.MultiConnectorSyncService>(),
                tenant,
                TimeSpan.FromMinutes(Math.Max(0, minutes)),
                async ct => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.MaxAsync(
                    db.SyncCursors.Where(c => c.TenantId == tenant && connectorIds.Contains(c.ProviderName)),
                    c => (DateTimeOffset?)c.LastSyncedAt, ct),
                crossProcessLock: async ct => await FlowLedger.Infrastructure.DataSafety.PostgresAdvisoryLock.TryAcquireAsync(
                    configuration.GetConnectionString("flowledger")!, "flowledger-sync:" + tenant, ct));

            // After every successful sync, fill in missing categories (never overwrites existing ones).
            var categorizing = new FlowLedger.Infrastructure.Sync.CategorizingSyncService(
                guarded,
                sp.GetRequiredService<FlowLedger.Application.Features.Categories.CategorizationService>(),
                sp.GetRequiredService<FlowLedger.Application.Features.Transactions.MerchantAliasService>(),
                sp.GetRequiredService<FlowLedger.Application.Features.Categories.CategoryRuleService>(),
                sp.GetRequiredService<FlowLedger.Application.Features.Transactions.TransactionEnrichmentService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FlowLedger.Infrastructure.Sync.CategorizingSyncService>>());

            // Then detect recurring income/bills (after categories exist, so transfers/trades are excluded).
            // Config: RecurringFlows:AutoAdd (default true) adds only fixed-amount, very regular patterns.
            var recurring = new FlowLedger.Infrastructure.Sync.RecurringFlowSyncService(
                categorizing,
                sp.GetRequiredService<FlowLedger.Application.Features.RecurringFlows.RecurringSuggestionService>(),
                !bool.TryParse(cfg["RecurringFlows:AutoAdd"], out var autoAdd) || autoAdd,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FlowLedger.Infrastructure.Sync.RecurringFlowSyncService>>());

            return new FlowLedger.Infrastructure.Sync.SheetRefreshingSyncService(
                recurring,
                sp.GetService<FlowLedger.Application.Abstractions.ISheetAutoMarker>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FlowLedger.Infrastructure.Sync.SheetRefreshingSyncService>>());
        });

        services.AddSingleton<FlowLedger.Application.Features.Users.IUserDirectory>(_ =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlUserDirectory(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                TenancySettings.Legacy(configuration)));

        services.AddScoped<FlowLedger.Application.Features.Categories.ICategoryMaintenanceStore>(sp =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlCategoryMaintenanceStore(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>()));
        services.AddScoped<FlowLedger.Application.Features.Categories.ICategoryRuleStore>(sp =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlCategoryRuleStore(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>()));
        services.AddScoped<FlowLedger.Application.Features.Transactions.IMerchantAliasStore>(sp =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlMerchantAliasStore(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>()));

        services.AddScoped<FlowLedger.Application.Features.Dashboard.ISuspiciousDismissalStore>(sp =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlSuspiciousDismissalStore(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>()));

        services.AddScoped<FlowLedger.Application.Features.RecurringFlows.IRecurringDismissalStore>(sp =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlRecurringDismissalStore(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>()));

        // -- Provider call ledger (append-only; feeds budgets + the usage page) --
        services.AddSingleton<FlowLedger.Infrastructure.DataSafety.NpgsqlProviderCallLedger>(_ =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlProviderCallLedger(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found.")));
        // -- Budgets (per-tenant, ad-hoc table; part of every snapshot/export) --
        services.AddSingleton(_ => new FlowLedger.Infrastructure.DataSafety.NpgsqlBudgetStore(
            configuration.GetConnectionString("flowledger")
            ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found.")));
        // -- Savings goals (per-tenant, ad-hoc table; part of every snapshot/export) --
        services.AddSingleton(_ => new FlowLedger.Infrastructure.DataSafety.NpgsqlGoalStore(
            configuration.GetConnectionString("flowledger")
            ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found.")));
        services.AddSingleton<FlowLedger.Application.Features.Budgets.IBudgetStore>(sp =>
            sp.GetRequiredService<FlowLedger.Infrastructure.DataSafety.NpgsqlBudgetStore>());
        services.AddSingleton<FlowLedger.Application.Features.Goals.IGoalStore>(sp =>
            sp.GetRequiredService<FlowLedger.Infrastructure.DataSafety.NpgsqlGoalStore>());
        // -- Home Assistant sharing (per-tenant settings + sent ledger; alerts only by default) --
        services.AddSingleton<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantStore>(_ =>
            new FlowLedger.Infrastructure.DataSafety.NpgsqlHomeAssistantStore(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
                configuration["HomeAssistant:EncryptionKey"]));
        services.AddHttpClient(FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender.CreateHandler)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantNetworkPolicy,
            FlowLedger.Infrastructure.DataSafety.HouseholdHomeAssistantNetworkPolicy>();
        services.AddSingleton<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantSender,
            FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender>();
        // -- Scheduled backups (verified snapshot + restore drill + restic off-site) --
        services.AddSingleton(_ => FlowLedger.Infrastructure.DataSafety.BackupSettings.From(configuration));
        services.AddSingleton(_ => new FlowLedger.Infrastructure.DataSafety.BackupRunStore(
            configuration.GetConnectionString("flowledger")
            ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found.")));
        services.AddSingleton(sp => new FlowLedger.Infrastructure.DataSafety.BackupRunner(
            configuration.GetConnectionString("flowledger")!,
            sp.GetRequiredService<FlowLedger.Infrastructure.DataSafety.BackupSettings>(),
            sp.GetRequiredService<FlowLedger.Infrastructure.DataSafety.BackupRunStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Backup")));
        services.AddSingleton<FlowLedger.Integrations.Abstractions.IProviderCallLedger>(sp =>
            sp.GetRequiredService<FlowLedger.Infrastructure.DataSafety.NpgsqlProviderCallLedger>());
        // ── Object storage ──────────────────────────────────────────────────────
        services.Configure<ObjectStorageOptions>(configuration.GetSection(ObjectStorageOptions.SectionName));
        services.AddSingleton<IObjectStorage, LocalDiskObjectStorage>();
        services.AddSingleton<IRawPayloadArchive, ObjectStorageRawPayloadArchive>();

        return services;
    }
}
