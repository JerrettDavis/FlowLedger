using FlowLedger.Api.Auth;
using FlowLedger.Api.Endpoints;
using FlowLedger.Api.Jobs;
using FlowLedger.Api.Middleware;
using FlowLedger.Api.Tenancy;
using FlowLedger.Application;
using FlowLedger.Infrastructure;
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
if (builder.Environment.IsDevelopment())
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
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDatabaseMigrationService();
}

// ── ProblemDetails (with TenantResolutionException → 401 mapping) ─────────────
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
// Process-global fixed windows (self-host: single client). If multi-client is needed,
// switch to GetPartition keyed by HttpContext.Connection.RemoteIpAddress.
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opts.AddFixedWindowLimiter("api", l =>
    {
        l.Window = TimeSpan.FromMinutes(1);
        l.PermitLimit = 300;
        l.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        l.QueueLimit = 10;
    });

    opts.AddFixedWindowLimiter("write", l =>
    {
        l.Window = TimeSpan.FromMinutes(1);
        l.PermitLimit = 60;
        l.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        l.QueueLimit = 5;
    });

    opts.AddFixedWindowLimiter("webhook", l =>
    {
        l.Window = TimeSpan.FromMinutes(1);
        l.PermitLimit = 30;
        l.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        l.QueueLimit = 0; // No queue; reject immediately.
    });
});

var app = builder.Build();

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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── Aspire health + liveness endpoints (anonymous, all environments) ─────────
app.MapDefaultEndpoints();

// ── Feature endpoint groups ──────────────────────────────────────────────────
app.MapStatusEndpoints();
app.MapAccountEndpoints();
app.MapTransactionEndpoints();
app.MapCategoryEndpoints();
app.MapRecurringFlowEndpoints();
app.MapSyncEndpoints();
app.MapMxEndpoints();
app.MapForecastEndpoints();
app.MapImportEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
