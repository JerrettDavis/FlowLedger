using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Application.Features.Transactions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

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

        // ── Handlers — Transactions ──────────────────────────────────────────────
        services.AddScoped<ListTransactionsHandler>();
        services.AddScoped<GetTransactionHandler>();
        services.AddScoped<CreateTransactionHandler>();
        services.AddScoped<FlowLedger.Application.Features.Categories.CategorizationService>();
        services.AddScoped<FlowLedger.Application.Features.Transactions.TransactionEnrichmentService>();
        services.AddScoped<MerchantAliasService>();
        services.AddScoped<FlowLedger.Application.Features.Categories.CategoryRuleService>();
        services.AddScoped<TransactionAnnotationService>();
        services.AddScoped<FlowLedger.Application.Features.Investments.InvestmentPerformanceService>();
        services.AddScoped<FlowLedger.Application.Features.Dashboard.GetDashboardActivityHandler>();
        services.AddScoped<FlowLedger.Application.Features.Insights.GetSpendingBreakdownHandler>();
        services.AddScoped<FlowLedger.Application.Features.Dashboard.DismissSuspiciousHandler>();

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
        services.AddScoped<GetCategoryHandler>();
        services.AddScoped<CreateCategoryHandler>();
        services.AddScoped<UpdateCategoryHandler>();
        services.AddScoped<DeleteCategoryHandler>();

        // ── Handlers — RecurringFlows ────────────────────────────────────────────
        services.AddScoped<ListRecurringFlowsHandler>();
        services.AddScoped<GetRecurringFlowHandler>();
        services.AddScoped<CreateRecurringFlowHandler>();
        services.AddScoped<RecurringSuggestionService>();
        services.AddScoped<UpdateRecurringFlowHandler>();
        services.AddScoped<DeactivateRecurringFlowHandler>();

        // ── Bill sheet (spreadsheet mode) ────────────────────────────────────────
        services.AddScoped<FlowLedger.Application.Features.BillSheet.BillSheetService>();
        services.AddScoped<FlowLedger.Application.Abstractions.ISheetAutoMarker>(
            sp => sp.GetRequiredService<FlowLedger.Application.Features.BillSheet.BillSheetService>());

        // ── Handlers — Imports and Matching ──────────────────────────────────────
        services.AddScoped<MatchingEngine>();
        services.AddScoped<ImportTransactionsHandler>();
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

        // ── FluentValidation ─────────────────────────────────────────────────────
        services.AddValidatorsFromAssemblyContaining<CreateAccountRequestValidator>(ServiceLifetime.Scoped);

        return services;
    }
}
