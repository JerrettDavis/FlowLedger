using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Application.Features.Categories;
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
        // ── Handlers — Accounts ──────────────────────────────────────────────────
        services.AddScoped<ListAccountsHandler>();
        services.AddScoped<GetAccountHandler>();
        services.AddScoped<CreateAccountHandler>();
        services.AddScoped<UpdateAccountHandler>();
        services.AddScoped<DeactivateAccountHandler>();
        services.AddScoped<UpdateAccountSettingsHandler>();
        services.AddScoped<ChangeAccountTypeHandler>();

        // ── Handlers — Transactions ──────────────────────────────────────────────
        services.AddScoped<ListTransactionsHandler>();
        services.AddScoped<GetTransactionHandler>();
        services.AddScoped<GetProcessingWindowHandler>();
        // Process-local, bounded: per-tenant data version + small result cache (#160). Replicas see a write after the cache TTL.
        services.TryAddSingleton<FlowLedger.Application.Abstractions.ITenantDataVersion, FlowLedger.Application.Abstractions.TenantDataVersion>();
        services.TryAddSingleton<FlowLedger.Application.Abstractions.TenantResultCache>();
        services.AddScoped<FlowLedger.Application.Abstractions.IReferenceValidator, FlowLedger.Application.Abstractions.ReferenceValidator>();
        services.AddScoped<CreateTransactionHandler>();
        services.TryAddSingleton<FlowLedger.Application.Features.Categories.CategorizationOptions>();
        services.AddScoped<FlowLedger.Application.Features.Query.AskQueryHandler>();
        services.TryAddSingleton<FlowLedger.Application.Features.Query.AskConcurrencyGate>();   // process-local, bounded by in-flight asks
        services.AddScoped<FlowLedger.Application.Features.Categories.CategorizationService>();
        services.TryAddSingleton<FlowLedger.Application.Features.LocalAi.LocalAiOptions>();
        services.AddScoped<FlowLedger.Application.Features.LocalAi.LocalAiService>();
        services.AddScoped<FlowLedger.Application.Features.Transactions.TransactionEnrichmentService>();
        services.AddScoped<MerchantAliasService>();
        services.AddScoped<FlowLedger.Application.Features.Strategies.StrategyService>();
        services.AddScoped<FlowLedger.Application.Features.Categories.CategoryRuleService>();
        services.AddScoped<FlowLedger.Application.Features.Categories.CategoryRestructureService>();
        services.AddScoped<TransactionAnnotationService>();
        services.AddScoped<FlowLedger.Application.Features.Investments.InvestmentPerformanceService>();
        services.AddScoped<FlowLedger.Application.Features.Dashboard.GetDashboardActivityHandler>();
        services.AddScoped<FlowLedger.Application.Features.Insights.GetSpendingBreakdownHandler>();
        services.AddScoped<FlowLedger.Application.Features.Dashboard.DismissSuspiciousHandler>();
        services.AddScoped<FlowLedger.Application.Features.Dashboard.GetDashboardInsightsHandler>();

        // Assets
        services.AddScoped<ListAssetsHandler>();
        services.AddScoped<GetAssetHandler>();
        services.AddScoped<CreateAssetHandler>();
        services.AddScoped<UpdateAssetHandler>();
        services.AddScoped<RecordAssetValuationHandler>();
        services.AddScoped<DeactivateAssetHandler>();
        services.AddScoped<GetAssetSummaryHandler>();
        services.AddScoped<RefreshAssetValuationHandler>();
        services.AddSingleton<FlowLedger.Application.Abstractions.IAssetValuationFeed, ModelEstimateFeed>();

        // Query and reports
        services.AddScoped<FlowLedger.Application.Features.Query.QueryDatasetProvider>();
        services.AddScoped<FlowLedger.Application.Features.Query.RunQueryHandler>();
        services.AddScoped<FlowLedger.Application.Features.Query.GetQuerySchemaHandler>();
        services.AddScoped<FlowLedger.Application.Features.Query.ListReportsHandler>();
        services.AddScoped<FlowLedger.Application.Features.Query.GetReportHandler>();
        services.AddScoped<FlowLedger.Application.Features.Query.SaveReportHandler>();
        services.AddScoped<FlowLedger.Application.Features.Query.RunReportHandler>();

        // ── Handlers — Categories ────────────────────────────────────────────────
        services.AddScoped<ListCategoriesHandler>();
        services.AddScoped<RestoreDefaultCategoriesHandler>();
        services.AddScoped<GetCategoryHandler>();
        services.AddScoped<CreateCategoryHandler>();
        services.AddScoped<UpdateCategoryHandler>();
        services.AddScoped<DeleteCategoryHandler>();

        // ── Handlers — RecurringFlows ────────────────────────────────────────────
        services.AddScoped<ListRecurringFlowsHandler>();
        services.AddScoped<GetRecurringFlowHandler>();
        services.AddScoped<CreateRecurringFlowHandler>();
        services.AddScoped<RecurringSuggestionService>();
        services.AddScoped<FlowLedger.Application.Features.Duplicates.DuplicateService>();
        services.AddScoped<UpdateRecurringFlowHandler>();
        services.AddScoped<DeactivateRecurringFlowHandler>();

        // ── Bill sheet (spreadsheet mode) ────────────────────────────────────────
        services.AddScoped<FlowLedger.Application.Features.BillSheet.BillSheetService>();
        services.AddScoped<FlowLedger.Application.Abstractions.ISheetAutoMarker>(
            sp => sp.GetRequiredService<FlowLedger.Application.Features.BillSheet.BillSheetService>());

        // ── Handlers — Imports and Matching ──────────────────────────────────────
        services.AddScoped<MatchingEngine>();
        services.AddScoped<ImportTransactionsHandler>();
        services.AddScoped<ImportCoordinator>();
        services.AddScoped<MatchTransactionHandler>();
        services.AddScoped<UnmatchTransactionHandler>();
        services.AddScoped<ListMatchSuggestionsHandler>();

        // ── Handlers — Forecasting ────────────────────────────────────────────────
        services.AddSingleton<IForecastEngine, ForecastEngine>();
        services.AddScoped<GetForecastHandler>();

        // ── Handlers — Budgets & Alerts ───────────────────────────────────────────
        services.AddScoped<FlowLedger.Application.Features.Budgets.GetMonthlySpendByCategoryHandler>();
        services.AddScoped<FlowLedger.Application.Features.Budgets.ListBudgetsHandler>();
        services.AddScoped<FlowLedger.Application.Features.Alerts.GetAlertsHandler>();
        services.AddScoped<FlowLedger.Application.Features.HomeAssistant.HomeAssistantService>();
        services.AddScoped<FlowLedger.Application.Features.Integrations.IntegrationService>();

        // ── FluentValidation ─────────────────────────────────────────────────────
        services.AddValidatorsFromAssemblyContaining<CreateAccountRequestValidator>(ServiceLifetime.Scoped);

        return services;
    }
}
