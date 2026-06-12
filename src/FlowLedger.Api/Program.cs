using FlowLedger.Api.Endpoints;
using FlowLedger.Api.Jobs;
using FlowLedger.Api.Tenancy;
using FlowLedger.Application;
using FlowLedger.Infrastructure;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

// ── Service defaults (OpenTelemetry, health checks, service discovery) ──────
builder.AddServiceDefaults();

// ── OpenAPI ─────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ── Tenant context ───────────────────────────────────────────────────────────
// Dev seam: resolved from X-Tenant-Id header with demo fallback.
// Replace with JWT-claims resolver in Milestone 5.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, DevTenantContext>();

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

// ── ProblemDetails ────────────────────────────────────────────────────────────
builder.Services.AddProblemDetails();

var app = builder.Build();

// ── Middleware ───────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseExceptionHandler(); // ProblemDetails for unhandled exceptions in dev
}
else
{
    app.UseExceptionHandler();
}

app.UseHttpsRedirection();

// ── Aspire health + liveness endpoints ──────────────────────────────────────
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
