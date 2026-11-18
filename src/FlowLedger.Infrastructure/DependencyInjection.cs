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

        var simpleFinEnabled = bool.TryParse(configuration["SimpleFin:Enabled"], out var sf) && sf;

        if (simpleFinEnabled)
        {
            // SimpleFIN Bridge (personal real-bank aggregation). Mutually exclusive with MX.
            if (mxOptions.Enabled)
            {
                throw new InvalidOperationException("Mx:Enabled and SimpleFin:Enabled cannot both be true.");
            }

            services.AddSimpleFinProvider(configuration);
        }
        else if (mxOptions.Enabled)
        {
            services.AddMxProvider(configuration);
        }
        else
        {
            services.AddSimulatedProvider(configuration);
        }

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
        services.AddScoped<IFinancialSyncService>(sp =>
        {
            var provider = sp.GetRequiredService<FlowLedger.Integrations.Abstractions.IFinancialDataProvider>();
            var tenant = sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>().TenantId;
            // Configured provider credentials belong to the household (legacy) tenant only — never sync them into another user's tenant.
            if (provider.ProviderName != "Simulated"
                && tenant != TenancySettings.Legacy(sp.GetRequiredService<IConfiguration>()).LegacyTenantId)
            {
                return new FlowLedger.Infrastructure.Sync.UnlinkedTenantSyncService();
            }
            var cfg = sp.GetRequiredService<IConfiguration>();
            // Real providers default to 30 min between syncs; the free Simulated provider is unthrottled.
            var defaultMinutes = provider.ProviderName == "Simulated" ? 0 : 30;
            var minutes = int.TryParse(cfg["Sync:MinIntervalMinutes"], out var m) ? m : defaultMinutes;
            var db = sp.GetRequiredService<FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext>();
            var providerName = provider.ProviderName;

            var guarded = new FlowLedger.Infrastructure.Sync.GuardedSyncService(
                sp.GetRequiredService<FinancialSyncService>(),
                tenant,
                TimeSpan.FromMinutes(Math.Max(0, minutes)),
                async ct => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.MaxAsync(
                    db.SyncCursors.Where(c => c.TenantId == tenant && c.ProviderName == providerName),
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
