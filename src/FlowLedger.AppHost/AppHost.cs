using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// ── Infrastructure resources ────────────────────────────────────────────────
// The database NAME is configurable so real data can live in its own database (and the data_environment guard
// then never sees demo rows): set FlowLedger:DatabaseName (e.g. "flowledger_prod") on the AppHost together with
// FlowLedger:DataDir for a separate raw-payload archive. Default stays "flowledger" (the demo database).
// FlowLedger:PostgresVolume names the data volume explicitly. Use a NEW name for real data so it never inherits
// the password/contents of an older demo volume (a persistent volume keeps the password it was first created with).
var pgVolume = builder.Configuration["FlowLedger:PostgresVolume"];
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(string.IsNullOrWhiteSpace(pgVolume) ? null : pgVolume, isReadOnly: false)
    .AddDatabase("flowledger", builder.Configuration["FlowLedger:DatabaseName"] ?? "flowledger");

var redis = builder.AddRedis("redis")
    .WithDataVolume(isReadOnly: false);

// ── MX config forwarding ────────────────────────────────────────────────────
// Set Mx:* once in the AppHost (via dotnet user-secrets or env vars) and it is
// forwarded to both api and worker.  When unset, nothing is forwarded and both
// services fall back to their Simulated-provider defaults.
//
// Example (AppHost user-secrets):
//   dotnet user-secrets set "Mx:Enabled"       "true"
//   dotnet user-secrets set "Mx:ApiKey"        "your-api-key"
//   dotnet user-secrets set "Mx:ClientId"      "your-client-id"
//   dotnet user-secrets set "Mx:BaseUrl"       "https://int-api.mx.com"
//   dotnet user-secrets set "Mx:WebhookSecret" "your-webhook-secret"
//
// To restore Simulated mode:
//   dotnet user-secrets clear    (or remove the individual Mx:* keys)
static IResourceBuilder<T> WithMxConfig<T>(
    IResourceBuilder<T> resource,
    IConfiguration configuration) where T : IResourceWithEnvironment
{
    // SAFE BY DEFAULT: service-level user-secrets (src/FlowLedger.Api|Worker) may hold MX creds with
    // Mx:Enabled=true. Env vars outrank secrets, so pin Simulated unless the AppHost config says
    // otherwise. Going live is a deliberate act: `dotnet user-secrets set Mx:Enabled true` on the AppHost.
    resource = resource.WithEnvironment("Mx__Enabled", configuration["Mx:Enabled"] ?? "false");

    var mxKeys = new[] { "ApiKey", "ClientId", "BaseUrl", "WebhookSecret", "Environment" };
    foreach (var key in mxKeys)
    {
        var value = configuration[$"Mx:{key}"];
        if (!string.IsNullOrEmpty(value))
        {
            resource = resource.WithEnvironment($"Mx__{key}", value);
        }
    }

    // SimpleFIN Bridge: same safe-by-default rule. Real bank data only when the AppHost config says so.
    resource = resource.WithEnvironment("SimpleFin__Enabled", configuration["SimpleFin:Enabled"] ?? "false");
    foreach (var key in new[] { "AccessUrl", "HistoryDays", "OverlapDays" })
    {
        var value = configuration[$"SimpleFin:{key}"];
        if (!string.IsNullOrEmpty(value))
        {
            resource = resource.WithEnvironment($"SimpleFin__{key}", value);
        }
    }

    // Home/real-estate valuation feed (generic HTTP JSON appraisal feed). Key lives in user-secrets, never in source.
    foreach (var key in new[] { "Enabled", "Name", "UrlTemplate", "ApiKeyHeader", "ApiKey", "ValuePath" })
    {
        var value = configuration[$"AssetFeeds:Http:{key}"];
        if (!string.IsNullOrEmpty(value))
        {
            resource = resource.WithEnvironment($"AssetFeeds__Http__{key}", value);
        }
    }

    // RealtyAPI.io (Redfin Estimate). Key in AppHost user-secrets: RealtyApi:ApiKey. When enabled it becomes
    // the monthly home-valuation feed.
    var realtyEnabled = string.Equals(configuration["RealtyApi:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    resource = resource.WithEnvironment("RealtyApi__Enabled", realtyEnabled ? "true" : "false");
    if (!string.IsNullOrEmpty(configuration["RealtyApi:ApiKey"]))
    {
        resource = resource.WithEnvironment("RealtyApi__ApiKey", configuration["RealtyApi:ApiKey"]!);
    }
    if (realtyEnabled)
    {
        resource = resource.WithEnvironment("AssetValuation__AutoRefresh__FeedName", "realtyapi");
    }

    // Tulsa County Assessor (public site, no key). Enabling registers the feed; it becomes the auto-refresh
    // feed only when RealtyAPI is not already selected.
    var tulsaEnabled = string.Equals(configuration["TulsaAssessor:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    resource = resource.WithEnvironment("TulsaAssessor__Enabled", tulsaEnabled ? "true" : "false");
    if (tulsaEnabled && !realtyEnabled)
    {
        resource = resource.WithEnvironment("AssetValuation__AutoRefresh__FeedName", "tulsa-assessor");
    }
    return resource;
}

// ── Raw provider payload archive ────────────────────────────────────────────
// Real provider data is billable and irreplaceable, so the archive lives OUTSIDE the repo/bin
// at a stable per-user path shared by api and worker. Override with FlowLedger:DataDir.
// Never point this at a temp directory. See docs/adr/0007-raw-payload-retention.md.
var dataDir = builder.Configuration["FlowLedger:DataDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".flowledger");
var objectsDir = Path.Combine(dataDir, "objects");
Directory.CreateDirectory(objectsDir);

// ── Application services ────────────────────────────────────────────────────
var api = builder.AddProject<Projects.FlowLedger_Api>("api")
    .WithReference(postgres)
    .WithReference(redis)
    .WaitFor(postgres)
    .WaitFor(redis);

WithMxConfig(api, builder.Configuration);
api.WithEnvironment("ObjectStorage__RootPath", objectsDir);

var web = builder.AddProject<Projects.FlowLedger_Web>("web")
    .WithReference(api)
    .WaitFor(api);

var worker = builder.AddProject<Projects.FlowLedger_Worker>("worker")
    .WithReference(postgres)
    .WithReference(redis)
    .WaitFor(postgres)
    .WaitFor(redis);

WithMxConfig(worker, builder.Configuration);
worker.WithEnvironment("ObjectStorage__RootPath", objectsDir);

// Tenancy (multi-user / multi-login): forwarded to every service that resolves identities or fans out over tenants,
// so they all agree on which tenant is the legacy household and who owns it. Pin LegacyTenantId explicitly so a
// Worker:TenantId override can never change which tenant the owner's logins adopt.
foreach (var svc in new IResourceBuilder<IResourceWithEnvironment>[] { api, worker })
{
    svc.WithEnvironment("Tenancy__LegacyTenantId", builder.Configuration["Tenancy:LegacyTenantId"] ?? "00000000-0000-0000-0000-000000000001");
    foreach (var key in new[] { "LegacyOwnerEmail", "LegacyOwnerSubject", "LegacyOwnerIssuer", "AutoLinkVerifiedEmail", "TrustUnverifiedEmail" })
    {
        if (!string.IsNullOrEmpty(builder.Configuration[$"Tenancy:{key}"]))
        {
            svc.WithEnvironment($"Tenancy__{key}", builder.Configuration[$"Tenancy:{key}"]!);
        }
    }
}

// Optional: point the worker at a dedicated tenant (e.g. an MX-sandbox tenant) so sandbox data never
// blends with the simulated demo tenant. API callers select the same tenant via X-Tenant-Id.
if (!string.IsNullOrEmpty(builder.Configuration["FlowLedger:WorkerTenantId"]))
{
    worker.WithEnvironment("Worker__TenantId", builder.Configuration["FlowLedger:WorkerTenantId"]!);
}

// Force unoptimized JIT on debugged service processes so the Rider/Aspire debugger can read locals.
// Without this the CLR runs ReadyToRun/tiered code and throws CORDBG_E_IL_VAR_NOT_AVAILABLE (0x80131304).
// IsRunMode is true only for local run/debug — never during publish or CI.
if (builder.ExecutionContext.IsRunMode)
{
    foreach (var svc in new[] { api, web, worker })
    {
        svc.WithEnvironment("DOTNET_TieredCompilation", "0")
           .WithEnvironment("DOTNET_ReadyToRun", "0")
           .WithEnvironment("DOTNET_TieredPGO", "0");
    }
}

builder.Build().Run();
