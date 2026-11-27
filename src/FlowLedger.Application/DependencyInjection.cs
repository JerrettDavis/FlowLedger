using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Application.Features.Transactions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowLedger.Application;

/// <summary>
/// Application-layer service registrations: handlers, validators, and service abstractions.
/// Called from the API and Worker hosts after Infrastructure is registered.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        AddHandlers(services);
        AddServices(services);

        // ── FluentValidation ─────────────────────────────────────────────────────
        services.AddValidatorsFromAssemblyContaining<CreateAccountRequestValidator>(ServiceLifetime.Scoped, includeInternalTypes: true);

        return services;
    }

    /// <summary>
    /// Every concrete, non-nested <c>*Handler</c> class in this assembly (public or internal) is a scoped use-case entry point: registered by convention so a
    /// new handler cannot be forgotten (a test also asserts every handler resolves).
    /// </summary>
    private static void AddHandlers(IServiceCollection services)
    {
        var handlers = typeof(DependencyInjection).Assembly.GetTypes() // internal handlers register too: a handler made internal must not silently drop out of DI
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false, IsNested: false }
                        && t.Name.EndsWith("Handler", StringComparison.Ordinal));
        foreach (var handler in handlers)
        {
            services.TryAddScoped(handler);
        }
    }

    private static void AddServices(IServiceCollection services)
    {
        // Process-local, bounded: per-tenant data version + small result cache (#160). Replicas see a write after the cache TTL.
        services.TryAddSingleton<FlowLedger.Application.Abstractions.ITenantDataVersion, FlowLedger.Application.Abstractions.TenantDataVersion>();
        services.TryAddSingleton<FlowLedger.Application.Abstractions.TenantResultCache>();
        services.AddScoped<FlowLedger.Application.Abstractions.IReferenceValidator, FlowLedger.Application.Abstractions.ReferenceValidator>();
        services.TryAddSingleton<FlowLedger.Application.Features.Categories.CategorizationOptions>();
        services.TryAddSingleton<FlowLedger.Application.Features.Query.AskConcurrencyGate>();   // process-local, bounded by in-flight asks
        services.TryAddSingleton<FlowLedger.Application.Features.LocalAi.LocalAiOptions>();

        // Transactions, categorization and local AI
        services.AddScoped<FlowLedger.Application.Features.Categories.CategorizationService>();
        services.AddScoped<FlowLedger.Application.Features.LocalAi.LocalAiService>();
        services.AddScoped<FlowLedger.Application.Features.Transactions.TransactionEnrichmentService>();
        services.AddScoped<MerchantAliasService>();
        services.AddScoped<FlowLedger.Application.Features.Strategies.StrategyService>();
        services.AddScoped<FlowLedger.Application.Features.Categories.CategoryRuleService>();
        services.AddScoped<FlowLedger.Application.Features.Categories.CategoryRestructureService>();
        services.AddScoped<TransactionAnnotationService>();
        services.AddScoped<FlowLedger.Application.Features.Investments.InvestmentPerformanceService>();

        // Assets
        services.AddSingleton<FlowLedger.Application.Abstractions.IAssetValuationFeed, ModelEstimateFeed>();

        // Query
        services.AddScoped<FlowLedger.Application.Features.Query.QueryDatasetProvider>();

        // Recurring flows and duplicates
        services.AddScoped<RecurringSuggestionService>();
        services.AddScoped<FlowLedger.Application.Features.Duplicates.DuplicateService>();

        // Bill sheet (spreadsheet mode)
        services.AddScoped<FlowLedger.Application.Features.BillSheet.BillSheetService>();
        services.AddScoped<FlowLedger.Application.Abstractions.ISheetAutoMarker>(
            sp => sp.GetRequiredService<FlowLedger.Application.Features.BillSheet.BillSheetService>());

        // Imports and matching
        services.AddScoped<MatchingEngine>();
        services.AddScoped<ImportCoordinator>();

        // Forecasting
        services.AddSingleton<IForecastEngine, ForecastEngine>();

        // Budgets (rollover applied once, shared by the budgets page, alerts and Home Assistant)
        services.AddScoped<FlowLedger.Application.Features.Budgets.EffectiveBudgetService>();

        // Home Assistant and outbound integrations (alert.raised webhooks reuse the Home Assistant detection and dedupe)
        services.AddScoped<FlowLedger.Application.Features.HomeAssistant.HomeAssistantService>();
        services.AddScoped<FlowLedger.Application.Features.Integrations.IntegrationService>();
        services.AddScoped<FlowLedger.Application.Features.HomeAssistant.IAlertEventSource>(
            sp => sp.GetRequiredService<FlowLedger.Application.Features.HomeAssistant.HomeAssistantService>());
        services.AddScoped<FlowLedger.Application.Features.Alerts.AlertDeliveryService>();
    }
}
