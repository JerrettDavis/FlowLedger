using FlowLedger.Api.Auth;
using FlowLedger.Api.Endpoints;
using FlowLedger.Api.Jobs;
using FlowLedger.Api.Middleware;
using FlowLedger.Api.Tenancy;
using FlowLedger.Application;
using FlowLedger.Infrastructure;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

// ── Local override file (gitignored, optional) ───────────────────────────────
// Gives operators an appsettings.{env}.local.json escape hatch for secrets
// (e.g. Api:Key, Mx:WebhookSecret) that must never be committed.
builder.Configuration.AddJsonFile(
    $"appsettings.{builder.Environment.EnvironmentName}.local.json",
    optional: true,
    reloadOnChange: false);

// ── Service defaults (OpenTelemetry, health checks, service discovery) ──────
builder.AddServiceDefaults();

// ── OpenAPI ─────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ── Tenant context ───────────────────────────────────────────────────────────
// Dev seam: DevTenantContext resolves X-Tenant-Id with a demo fallback.
// Non-Development: HeaderTenantContext fails closed (throws TenantResolutionException
// → 401) when no valid X-Tenant-Id header is supplied.
builder.Services.AddHttpContextAccessor();
// Tenancy:RequireTenantHeader=true makes even Development fail closed (no demo-tenant fallback) - set it on any stack with real users.
if (builder.Environment.IsDevelopment()
    && !builder.Configuration.GetValue("Tenancy:RequireTenantHeader", false)
    && string.IsNullOrEmpty(builder.Configuration["Api:TenantSigningKey"]))
{
    builder.Services.AddScoped<ITenantContext, DevTenantContext>();
}
else
{
    builder.Services.AddScoped<ITenantContext, HeaderTenantContext>();
}

// ── Infrastructure (EF Core, Postgres, provider wiring, repositories) ────────
builder.Services.AddInfrastructure(builder.Configuration);

// ── Application (handlers, validators) ───────────────────────────────────────
builder.Services.AddApplication();

// ── Quartz (on-demand sync enqueued by verified MX webhooks) ─────────────────
// The API hosts a durable on-demand job triggered from the MX webhook endpoint.
// The recurring scheduled sync remains owned by the Worker host.
builder.Services.AddQuartz(q =>
{
    q.AddJob<OnDemandSyncJob>(opts => opts.WithIdentity(OnDemandSyncJob.Key).StoreDurably());
});
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = false);

// ── Dev-mode auto-migration ──────────────────────────────────────────────────
// Migrations are applied automatically only in Development to enable `dotnet run`
// to work without a separate migration step. Production deployments use explicit
// migration tooling (CI step or Aspire migration container).
// Database:MigrateOnStartup=true opts a non-Development host in (docker-compose sets it: it has no migration job).
if (builder.Environment.IsDevelopment() || builder.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    builder.Services.AddDatabaseMigrationService();
}

// Refuse to start when the database's data_environment marker and Mx config disagree across the
// production boundary. Registered AFTER the migration service so the schema exists first.
builder.Services.AddDataEnvironmentGuard();

// ── ProblemDetails (TenantResolutionException → 401; any ProviderException → ProviderExceptionHandler, ADR 0018) ──
builder.Services.AddExceptionHandler<FlowLedger.Api.Errors.ProviderExceptionHandler>();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        if (ctx.Exception is TenantResolutionException)
        {
            ctx.ProblemDetails.Status = StatusCodes.Status401Unauthorized;
            ctx.ProblemDetails.Title = "Tenant identification required";
            ctx.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
    };
});

// ── API key authentication ────────────────────────────────────────────────────
// Custom ApiKey scheme (Authorization: Bearer {key} or X-Api-Key: {key}).
// See docs/adr/0001-auth-openiddict-deferred.md for why full OpenIddict is deferred.
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();

    // FallbackPolicy protects everything by default — endpoints opt out via AllowAnonymous().
    options.DefaultPolicy = policy;
    options.FallbackPolicy = policy;
});

// ── Api options + data-annotation validation at startup ──────────────────────
builder.Services.AddOptions<ApiOptions>()
    .BindConfiguration(ApiOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ── Rate limiting ─────────────────────────────────────────────────────────────
// Fixed windows partitioned per tenant (authenticated) or per remote IP (anonymous), so one user or one
// noisy caller cannot exhaust everyone's budget. See RateLimitPartitions.
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opts.AddFixedWindowPolicy("api", permitLimit: 300, queueLimit: 10);
    opts.AddFixedWindowPolicy("write", permitLimit: 60, queueLimit: 5);
    opts.AddFixedWindowPolicy("webhook", permitLimit: 30, queueLimit: 0); // No queue; reject immediately.
});

var app = builder.Build();

// A signing key only adds protection if it is strong and distinct from the API key; refuse to start with a useless one.
{
    var apiOpts = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
    if (!string.IsNullOrEmpty(apiOpts.TenantSigningKey)
        && (apiOpts.TenantSigningKey.Length < 32 || string.Equals(apiOpts.TenantSigningKey, apiOpts.Key, StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            "Api:TenantSigningKey must be at least 32 characters and different from Api:Key (e.g. `openssl rand -hex 32`).");
    }
}

// ── Production startup guard (secret hygiene) ────────────────────────────────
// Refuse to start a non-Development instance with a missing or default API key.
if (!app.Environment.IsDevelopment())
{
    var apiOptions = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;

    if (string.IsNullOrWhiteSpace(apiOptions.Key))
    {
        throw new InvalidOperationException(
            "Api:Key must be set to a non-empty value outside Development. " +
            "Set it via environment variable Api__Key or appsettings.<Env>.local.json.");
    }

    const string devDefault = "dev-local-key-not-for-production";
    if (apiOptions.Key.Equals(devDefault, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Api:Key is the development default in a non-Development environment. " +
            "Generate a strong random key (e.g. `openssl rand -hex 32`) and set Api__Key.");
    }
}

if (app.Environment.IsDevelopment())
{
    var devKey = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value.Key;
    if (string.Equals(devKey, "dev-local-key-not-for-production", StringComparison.Ordinal))
    {
        app.Logger.LogWarning(
            "SECURITY: running in Development with the public dev API key, and requests without X-Tenant-Id fall back to the " +
            "household tenant. Do not expose this instance or onboard other users until ASPNETCORE_ENVIRONMENT=Production (or at least Tenancy__RequireTenantHeader=true) and a real Api__Key are set.");
    }
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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

// ── Aspire health + liveness endpoints (anonymous, all environments) ─────────
app.MapDefaultEndpoints();

// ── Feature endpoint groups ──────────────────────────────────────────────────
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

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
