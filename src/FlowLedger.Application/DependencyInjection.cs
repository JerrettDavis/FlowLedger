using FlowLedger.Application.Features.Accounts;
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
        services.AddScoped<UpdateRecurringFlowHandler>();
        services.AddScoped<DeactivateRecurringFlowHandler>();

        // ── Handlers — Imports and Matching ──────────────────────────────────────
        services.AddScoped<MatchingEngine>();
        services.AddScoped<ImportTransactionsHandler>();
        services.AddScoped<MatchTransactionHandler>();
        services.AddScoped<UnmatchTransactionHandler>();
        services.AddScoped<ListMatchSuggestionsHandler>();

        // ── Handlers — Forecasting ────────────────────────────────────────────────
        services.AddSingleton<IForecastEngine, ForecastEngine>();
        services.AddScoped<GetForecastHandler>();

        // ── FluentValidation ─────────────────────────────────────────────────────
        services.AddValidatorsFromAssemblyContaining<CreateAccountRequestValidator>(ServiceLifetime.Scoped);

        return services;
    }
}
