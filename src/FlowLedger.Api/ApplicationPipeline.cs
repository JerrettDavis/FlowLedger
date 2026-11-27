using FlowLedger.Api.Auth;
using FlowLedger.Api.Endpoints;
using FlowLedger.Api.Middleware;
using FlowLedger.ServiceDefaults;
using FlowLedger.SharedKernel;
using Microsoft.Extensions.Options;

namespace FlowLedger.Api;

/// <summary>Startup guards, middleware order and endpoint mapping for the API host, so Program.cs stays a table of contents (#94).</summary>
internal static class ApplicationPipeline
{
    /// <summary>
    /// Refuses to start with a useless or placeholder secret: a signing key only adds protection if it is strong and distinct from
    /// the API key, and a non-Development instance needs a real API key (secret hygiene).
    /// </summary>
    internal static void ValidateStartupSecrets(this WebApplication app)
    {
        var apiOptions = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
        if (!string.IsNullOrEmpty(apiOptions.TenantSigningKey)
            && (apiOptions.TenantSigningKey.Length < 32
                || string.Equals(apiOptions.TenantSigningKey, apiOptions.Key, StringComparison.Ordinal)
                || string.Equals(apiOptions.TenantSigningKey, apiOptions.PreviousKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Api:TenantSigningKey must be at least 32 characters and different from Api:Key (e.g. `openssl rand -hex 32`).");
        }

        if (!app.Environment.IsDevelopment())
        {
            // Strength (>= 32 chars, no well-known placeholder) for Api:Key and, during rotation, Api:PreviousKey.
            if (ApiKeyPolicy.ValidateOptions(apiOptions) is { } keyError)
            {
                throw new InvalidOperationException(
                    keyError + " Set it via environment variable Api__Key or appsettings.<Env>.local.json.");
            }

            if (ConfigGuards.LooksLikePlaceholder(apiOptions.Key))
            {
                throw new InvalidOperationException(
                    "Api:Key still contains a template placeholder (e.g. from .env.example). Generate a strong random key (`openssl rand -hex 32`).");
            }
        }
        else if (string.Equals(apiOptions.Key, "dev-local-key-not-for-production", StringComparison.Ordinal))
        {
            ApiLog.DevKeyInUse(app.Logger);
        }
    }

    internal static WebApplication UseFlowLedgerPipeline(this WebApplication app, bool trustForwardedHeaders)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseFlowLedgerForwardedHeaders(trustForwardedHeaders);
        app.UseExceptionHandler();
        app.UseSecureHeaders();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseAuthentication();
        app.UseRateLimiter(); // after authentication: partitions trust the tenant header only for valid API-key callers
        app.UseAuthorization();
        app.UseMiddleware<AuditMiddleware>(); // append-only audit_events row for sensitive endpoints
        return app;
    }

    internal static WebApplication MapFlowLedgerEndpoints(this WebApplication app)
    {
        // Aspire health + liveness endpoints (anonymous, all environments).
        app.MapDefaultEndpoints();

        app.MapStatusEndpoints();
        app.MapUserEndpoints();
        app.MapEnvironmentEndpoints();
        app.MapDataEndpoints();
        app.MapCategorizationEndpoints();
        app.MapLocalAiEndpoints();
        app.MapDuplicateEndpoints();
        app.MapMerchantAliasEndpoints();
        app.MapBudgetEndpoints();
        app.MapGoalEndpoints();
        app.MapDebtEndpoints();
        app.MapAlertEndpoints();
        app.MapHomeAssistantEndpoints();
        app.MapIntegrationEndpoints();
        app.MapCategoryRuleEndpoints();
        app.MapTransactionAnnotationEndpoints();
        app.MapInvestmentEndpoints();
        app.MapAccountEndpoints();
        app.MapAssetEndpoints();
        app.MapSheetEndpoints();
        app.MapTransactionEndpoints();
        app.MapDashboardEndpoints();
        app.MapInsightsEndpoints();
        app.MapStrategyEndpoints();
        app.MapQueryEndpoints();
        app.MapCategoryEndpoints();
        app.MapRecurringFlowEndpoints();
        app.MapSyncEndpoints();
        app.MapConnectorEndpoints();
        app.MapAccountConnectorEndpoints();
        app.MapMxEndpoints();
        app.MapQuilttEndpoints();
        app.MapForecastEndpoints();
        app.MapImportEndpoints();
        return app;
    }
}
