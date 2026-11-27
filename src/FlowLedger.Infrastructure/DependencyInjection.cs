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
        AddCategorizationAndLocalAi(services, configuration);
        AddPersistence(services, configuration);
        AddProvidersAndSync(services, configuration);
        AddStorageAndHealth(services, configuration);
        services.AddPostgresVersionGuard();

        return services;
    }

    /// <summary>ML categorizer tuning and the in-process local AI stack.</summary>
    private static void AddCategorizationAndLocalAi(IServiceCollection services, IConfiguration configuration)
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
        // Events are written to the transactional outbox inside SaveChanges (#92) and delivered by OutboxProcessor
        // (run by the Worker), which resolves every IDomainEventHandler<TEvent> registration. The context no longer dispatches inline.
        services.AddScoped<IDomainEventDispatcher, NoOpDomainEventDispatcher>();
        services.AddSingleton(configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions());
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

    /// <summary>Connector registry, provider wiring, asset feeds, sync pipeline, ad-hoc stores and backups (shares the connector list).</summary>
    private static void AddProvidersAndSync(IServiceCollection services, IConfiguration configuration)
    {
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
        var quilttEnabled = bool.TryParse(configuration["Quiltt:Enabled"], out var qt) && qt;
        var simulatedEnabled = (!simpleFinEnabled && !mxOptions.Enabled && !quilttEnabled) || simulatedExplicit;

        // The first enabled connector (SimpleFIN, MX, Simulated) is also the un-keyed "primary" IFinancialDataProvider for older call sites.
        var connectors = new List<FlowLedger.Integrations.Abstractions.ConnectorDescriptor>();
        if (simpleFinEnabled)
        {
            services.AddSimpleFinProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(new(FlowLedger.Integrations.Abstractions.ConnectorIds.SimpleFin, "SimpleFIN Bridge", "Aggregator (personal)", !FlowLedger.Integrations.SimpleFin.SimpleFinFinancialDataProvider.DeclaredTraits.HasFlag(FlowLedger.Integrations.Abstractions.ConnectorTraits.PerTenantAccess), FlowLedger.Integrations.SimpleFin.SimpleFinFinancialDataProvider.DeclaredTraits));
        }

        if (mxOptions.Enabled)
        {
            services.AddMxProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(new(FlowLedger.Integrations.Abstractions.ConnectorIds.Mx, "MX", "Aggregator (widget)", !FlowLedger.Integrations.Mx.MxFinancialDataProvider.DeclaredTraits.HasFlag(FlowLedger.Integrations.Abstractions.ConnectorTraits.PerTenantAccess), FlowLedger.Integrations.Mx.MxFinancialDataProvider.DeclaredTraits));
        }

        if (quilttEnabled)
        {
            services.AddQuilttProvider(configuration, registerAsDefault: connectors.Count == 0);
            // Per-user profiles: Quiltt isolates each tenant itself, so unlike SimpleFIN/MX it is not tied to the household tenant.
            connectors.Add(new(FlowLedger.Integrations.Abstractions.ConnectorIds.Quiltt, "Quiltt", "Aggregator (embedded)", !FlowLedger.Integrations.Quiltt.QuilttFinancialDataProvider.DeclaredTraits.HasFlag(FlowLedger.Integrations.Abstractions.ConnectorTraits.PerTenantAccess), FlowLedger.Integrations.Quiltt.QuilttFinancialDataProvider.DeclaredTraits));
        }

        if (simulatedEnabled)
        {
            services.AddSimulatedProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(new(FlowLedger.Integrations.Abstractions.ConnectorIds.Simulated, "Simulated demo data", "Demo", !FlowLedger.Integrations.Simulated.SimulatedFinancialDataProvider.DeclaredTraits.HasFlag(FlowLedger.Integrations.Abstractions.ConnectorTraits.PerTenantAccess), FlowLedger.Integrations.Simulated.SimulatedFinancialDataProvider.DeclaredTraits));
        }

        services.AddScoped<FlowLedger.Application.Abstractions.IHouseholdTenantAccess, HouseholdTenantAccess>();

        services.AddScoped<FlowLedger.Application.Abstractions.IAccountConnectorLinkStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfAccountConnectorLinkStore>();

        services.AddScoped<FlowLedger.Application.Abstractions.IProviderAccountCatalog,
            FlowLedger.Infrastructure.Persistence.Repositories.EfProviderAccountCatalog>();

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
        services.AddScoped<FlowLedger.Application.Features.Duplicates.IDuplicateStore, FlowLedger.Infrastructure.Persistence.Repositories.DuplicateStore>();
        services.AddSingleton(new FlowLedger.Application.Features.Duplicates.DuplicateOptions
        {
            AutoMerge = !bool.TryParse(configuration["Duplicates:AutoMerge"], out var dupAuto) || dupAuto,
        });
        services.AddScoped<FlowLedger.Application.Features.Categories.ICategorizationStore, FlowLedger.Infrastructure.Persistence.Repositories.CategorizationStore>();
        // Decorators (outer -> inner): outbox SyncCompleted -> recurring detection -> categorization -> cost guard (min interval + single-flight) -> sync.
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
            var defaultMinutes = usable.Any(c => !c.Traits.HasFlag(FlowLedger.Integrations.Abstractions.ConnectorTraits.SeedsDemoData)) ? 30 : 0;
            var minutes = int.TryParse(cfg["Sync:MinIntervalMinutes"], out var m) ? m : defaultMinutes;
            var db = sp.GetRequiredService<FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext>();
            var connectorIds = usable.Select(c => c.Id).ToArray();

            var guarded = new FlowLedger.Infrastructure.Sync.GuardedSyncService(
                sp.GetRequiredService<FlowLedger.Infrastructure.Sync.MultiConnectorSyncService>(),
                tenant,
                TimeSpan.FromMinutes(Math.Max(0, minutes)),
                async ct => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.MaxAsync(
                    db.SyncCursors.Where(c => c.TenantId == tenant && connectorIds.Contains(c.ProviderName)),
                    c => c.LastSuccessfulSyncAt, ct),
                crossProcessLock: async ct => await FlowLedger.Infrastructure.DataSafety.PostgresAdvisoryLock.TryAcquireAsync(
                    configuration.GetConnectionString("flowledger")!, "flowledger-sync:" + tenant, ct));

            // After every successful sync, fill in missing categories (never overwrites existing ones).
            // Duplicate merge, bill-sheet refresh, webhooks and Home Assistant alerts are not inline steps any more: the outermost
            // decorator records SyncCompleted in the outbox and idempotent handlers do them with retry (#92, #175, #180).
            var categorizing = new FlowLedger.Infrastructure.Sync.CategorizingSyncService(
                guarded,
                db,
                sp.GetRequiredService<FlowLedger.Application.Features.Categories.CategorizationService>(),
                sp.GetRequiredService<FlowLedger.Application.Features.Transactions.MerchantAliasService>(),
                sp.GetRequiredService<FlowLedger.Application.Features.Categories.CategoryRuleService>(),
                sp.GetRequiredService<FlowLedger.Application.Features.Transactions.TransactionEnrichmentService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FlowLedger.Infrastructure.Sync.CategorizingSyncService>>());

            // Then detect recurring income/bills (after categories exist, so transfers/trades are excluded).
            // Config: RecurringFlows:AutoAdd (default true) adds only fixed-amount, very regular patterns.
            var recurring = new FlowLedger.Infrastructure.Sync.RecurringFlowSyncService(
                categorizing,
                db,
                sp.GetRequiredService<FlowLedger.Application.Features.RecurringFlows.RecurringSuggestionService>(),
                !bool.TryParse(cfg["RecurringFlows:AutoAdd"], out var autoAdd) || autoAdd,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FlowLedger.Infrastructure.Sync.RecurringFlowSyncService>>());

            return new FlowLedger.Infrastructure.Sync.OutboxPublishingSyncService(
                recurring,
                sp.GetRequiredService<FlowLedger.Application.Abstractions.IOutbox>(),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>());
        });

        services.AddSingleton<FlowLedger.Application.Features.Users.IUserDirectory>(_ =>
            new FlowLedger.Infrastructure.DataSafety.EfUserDirectory(
                new FlowLedger.Infrastructure.Persistence.HostDb(
                    configuration.GetConnectionString("flowledger")
                    ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found.")),
                TenancySettings.Legacy(configuration)));
        services.AddSingleton<FlowLedger.Application.Abstractions.IWebhookReplayStore>(_ =>
            new FlowLedger.Infrastructure.DataSafety.EfWebhookReplayStore(
                new FlowLedger.Infrastructure.Persistence.HostDb(
                    configuration.GetConnectionString("flowledger")
                    ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."))));
        services.AddSingleton<FlowLedger.Application.Features.Users.ITenantRegistry>(_ =>
            new FlowLedger.Infrastructure.DataSafety.EfTenantRegistry(
                new FlowLedger.Infrastructure.Persistence.HostDb(
                    configuration.GetConnectionString("flowledger")
                    ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."))));
        services.AddScoped<FlowLedger.Application.Features.Users.ITenantErasure>(sp =>
            new FlowLedger.Infrastructure.DataSafety.EfTenantErasure(
                sp.GetRequiredService<FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext>(),
                TenancySettings.Legacy(configuration).LegacyTenantId,
                sp.GetServices<FlowLedger.Application.Features.Users.ITenantFileEraser>()));

        services.AddScoped<FlowLedger.Application.Features.Categories.ICategoryMaintenanceStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfCategoryMaintenanceStore>();
        services.AddScoped<FlowLedger.Application.Features.Categories.ICategoryRuleStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfCategoryRuleStore>();
        services.AddScoped<FlowLedger.Application.Features.Transactions.IMerchantAliasStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfMerchantAliasStore>();

        services.AddScoped<FlowLedger.Application.Features.Dashboard.ISuspiciousDismissalStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfSuspiciousDismissalStore>();

        services.AddScoped<FlowLedger.Application.Features.RecurringFlows.IRecurringDismissalStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfRecurringDismissalStore>();

        // -- Provider call ledger (append-only; feeds budgets + the usage page) --
        services.AddSingleton<FlowLedger.Infrastructure.DataSafety.ProviderCallLedger>(_ =>
            new FlowLedger.Infrastructure.DataSafety.ProviderCallLedger(
                configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found.")));
        // -- Audit log of sensitive actions (append-only, host-wide EF table) --
        services.AddSingleton<FlowLedger.Infrastructure.DataSafety.IAuditLog>(_ =>
            new FlowLedger.Infrastructure.DataSafety.EfAuditLog(
                new FlowLedger.Infrastructure.Persistence.HostDb(
                    configuration.GetConnectionString("flowledger")
                    ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."))));
        // -- Durable background job run history (host-wide EF table; worker writes, API reads) --
        services.AddSingleton<FlowLedger.Infrastructure.DataSafety.IJobRunStore>(_ =>
            new FlowLedger.Infrastructure.DataSafety.EfJobRunStore(
                new FlowLedger.Infrastructure.Persistence.HostDb(
                    configuration.GetConnectionString("flowledger")
                    ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."))));
        // -- Budgets and savings goals (per-tenant, EF: tenant filter + guard) --
        services.AddScoped<FlowLedger.Application.Features.Budgets.IBudgetStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfBudgetStore>();
        services.AddScoped<FlowLedger.Application.Features.Goals.IGoalStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfGoalStore>();
        // -- Account rates (per-tenant, ad-hoc table; part of every snapshot/export) --
        services.AddScoped<FlowLedger.Application.Features.Strategies.IAccountRateStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfAccountRateStore>();
        // -- Home Assistant sharing (per-tenant settings + sent ledger; alerts only by default) --
        services.AddScoped<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantStore>(sp =>
            new FlowLedger.Infrastructure.Persistence.Repositories.EfHomeAssistantStore(
                sp.GetRequiredService<FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext>(),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>(),
                ring: SecretsKeyRing(configuration, "HomeAssistant:EncryptionKey")));
        services.AddHttpClient(FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender.CreateHandler)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantNetworkPolicy,
            FlowLedger.Infrastructure.DataSafety.HouseholdHomeAssistantNetworkPolicy>();
        services.AddSingleton<FlowLedger.Application.Features.HomeAssistant.IHomeAssistantSender,
            FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender>();
        // -- Integrations (REST / SOAP / signed webhooks; per-tenant definitions, encrypted secrets, delivery log) --
        services.AddScoped<FlowLedger.Application.Features.Integrations.IIntegrationStore>(sp =>
            new FlowLedger.Infrastructure.Persistence.Repositories.EfIntegrationStore(
                sp.GetRequiredService<FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext>(),
                sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>(),
                ring: SecretsKeyRing(configuration, "Integrations:EncryptionKey", "HomeAssistant:EncryptionKey")));
        foreach (var clientName in new[]
        {
            FlowLedger.Infrastructure.DataSafety.IntegrationTransport.HttpClientName,
            FlowLedger.Infrastructure.DataSafety.IntegrationTransport.LocalHttpClientName,
        })
        {
            services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(FlowLedger.Infrastructure.DataSafety.HomeAssistantWebhookSender.CreateHandler)
                .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));
        }
        services.AddSingleton<FlowLedger.Application.Features.Integrations.IIntegrationTransport,
            FlowLedger.Infrastructure.DataSafety.IntegrationTransport>();
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
        services.AddSingleton(_ => new FlowLedger.Infrastructure.DataSafety.DataRetention(
            configuration.GetConnectionString("flowledger")
            ?? throw new InvalidOperationException("Required connection string 'flowledger' was not found."),
            FlowLedger.Infrastructure.DataSafety.RetentionSettings.From(configuration)));
        services.AddSingleton<FlowLedger.Integrations.Abstractions.IProviderCallLedger>(sp =>
            sp.GetRequiredService<FlowLedger.Infrastructure.DataSafety.ProviderCallLedger>());
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
