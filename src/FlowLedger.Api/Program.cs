using FlowLedger.Api;
using FlowLedger.Application;
using FlowLedger.Infrastructure;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.ServiceDefaults;

// One-shot schema migration (`dotnet FlowLedger.Api.dll --migrate`): the compose `migrate` service runs this and exits, so the
// long-running API need not auto-migrate in production (#195).
if (args.Contains(MigrateCommand.Flag))
{
    Environment.ExitCode = await MigrateCommand.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Local override file (gitignored, optional): an appsettings.{env}.local.json escape hatch for secrets
// (e.g. Api:Key, Mx:WebhookSecret) that must never be committed.
builder.Configuration.AddJsonFile(
    $"appsettings.{builder.Environment.EnvironmentName}.local.json",
    optional: true,
    reloadOnChange: false);

builder.AddServiceDefaults();                                                         // OpenTelemetry, health checks, service discovery
builder.Services.AddOpenApi();
builder.Services.AddFlowLedgerTenancy(builder.Environment, builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);                            // EF Core, Postgres, connectors, sync, stores
builder.Services.AddApplication();                                                    // handlers, validators
builder.Services.AddFlowLedgerQuartz();                                               // on-demand sync enqueued by verified webhooks

// Migrations are applied automatically only in Development (so `dotnet run` works), or when Database:MigrateOnStartup=true opts a
// host in (docker-compose sets it: it has no migration job). The data-environment guard is registered AFTER the migration service so
// the schema exists first; it refuses to start when the database marker and the MX config disagree across the production boundary.
if (builder.Environment.IsDevelopment() || builder.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    builder.Services.AddDatabaseMigrationService();
}

builder.Services.AddDataEnvironmentGuard();
builder.Services.AddFlowLedgerErrorHandling();                                        // ProblemDetails + central exception handlers
builder.Services.AddFlowLedgerAuthentication();
builder.Services.AddFlowLedgerRateLimiting(builder.Configuration);

// Opt-in forwarded headers (ForwardedHeaders:Enabled + KnownProxies/KnownNetworks): without it, behind a proxy the rate limiter and
// the loopback guard see the proxy address.
var trustForwardedHeaders = builder.AddFlowLedgerForwardedHeaders();

var app = builder.Build();

app.ValidateStartupSecrets();
app.UseFlowLedgerPipeline(trustForwardedHeaders);
app.MapFlowLedgerEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
