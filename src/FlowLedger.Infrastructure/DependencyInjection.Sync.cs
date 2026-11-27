using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure;

// Asset feeds and the sync pipeline registration groups (#108).
public static partial class DependencyInjection
{
    /// <summary>Asset valuation feeds: generic HTTP JSON feed, RealtyAPI.io and the Tulsa County assessor (each disabled unless configured).</summary>
    private static void AddAssetFeeds(IServiceCollection services, IConfiguration configuration)
    {
        // Generic HTTP JSON feed; disabled unless AssetFeeds:Http:Enabled.
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
    }

    /// <summary>The sync service, its decorator chain and the stores it needs.</summary>
    private static void AddSyncPipeline(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<FinancialSyncService>();
        services.AddScoped<IBalanceHistoryReader, FlowLedger.Infrastructure.Persistence.Repositories.BalanceHistoryReader>();
        services.AddScoped<FlowLedger.Application.Features.Duplicates.IDuplicateStore, FlowLedger.Infrastructure.Persistence.Repositories.DuplicateStore>();
        services.AddSingleton(new FlowLedger.Application.Features.Duplicates.DuplicateOptions
        {
            AutoMerge = !bool.TryParse(configuration["Duplicates:AutoMerge"], out var dupAuto) || dupAuto,
        });
        services.AddScoped<FlowLedger.Application.Features.Categories.ICategorizationStore, FlowLedger.Infrastructure.Persistence.Repositories.CategorizationStore>();
        services.AddScoped<MultiConnectorSyncService>();
        services.AddScoped<IFinancialSyncService>(sp => BuildSyncService(sp, configuration));
    }

    /// <summary>
    /// Decorators (outer -> inner): outbox SyncCompleted -> recurring detection -> categorization -> cost guard (min interval +
    /// single-flight) -> multi-connector sync.
    /// </summary>
    private static IFinancialSyncService BuildSyncService(IServiceProvider sp, IConfiguration configuration)
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var registry = sp.GetRequiredService<IConnectorRegistry>();
        var tenant = sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>().TenantId;
        // Operator-held connector credentials belong to the household (legacy) tenant only - never sync them into another user's tenant.
        var usable = MultiConnectorSyncService.UsableFor(registry, tenant, TenancySettings.Legacy(cfg).LegacyTenantId);
        if (usable.Count == 0)
        {
            return new UnlinkedTenantSyncService();
        }

        // Real connectors default to 30 min between syncs; the free Simulated provider is unthrottled.
        var defaultMinutes = usable.Any(c => !c.Traits.HasFlag(ConnectorTraits.SeedsDemoData)) ? 30 : 0;
        var minutes = int.TryParse(cfg["Sync:MinIntervalMinutes"], out var m) ? m : defaultMinutes;
        var db = sp.GetRequiredService<FlowLedgerDbContext>();
        var connectorIds = usable.Select(c => c.Id).ToArray();

        var guarded = new GuardedSyncService(
            sp.GetRequiredService<MultiConnectorSyncService>(),
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
        var categorizing = new CategorizingSyncService(
            guarded,
            db,
            sp.GetRequiredService<FlowLedger.Application.Features.Categories.CategorizationService>(),
            sp.GetRequiredService<FlowLedger.Application.Features.Transactions.MerchantAliasService>(),
            sp.GetRequiredService<FlowLedger.Application.Features.Categories.CategoryRuleService>(),
            sp.GetRequiredService<FlowLedger.Application.Features.Transactions.TransactionEnrichmentService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CategorizingSyncService>>(),
            tenant);

        // Then detect recurring income/bills (after categories exist, so transfers/trades are excluded).
        // Config: RecurringFlows:AutoAdd (default true) adds only fixed-amount, very regular patterns.
        var recurring = new RecurringFlowSyncService(
            categorizing,
            db,
            sp.GetRequiredService<FlowLedger.Application.Features.RecurringFlows.RecurringSuggestionService>(),
            !bool.TryParse(cfg["RecurringFlows:AutoAdd"], out var autoAdd) || autoAdd,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RecurringFlowSyncService>>());

        return new OutboxPublishingSyncService(recurring, sp.GetRequiredService<IOutbox>(), sp.GetRequiredService<FlowLedger.SharedKernel.ITenantContext>());
    }
}
