using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// ── Infrastructure resources ────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(isReadOnly: false)
    .AddDatabase("flowledger");

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
    var mxKeys = new[] { "Enabled", "ApiKey", "ClientId", "BaseUrl", "WebhookSecret", "Environment" };
    foreach (var key in mxKeys)
    {
        var value = configuration[$"Mx:{key}"];
        if (!string.IsNullOrEmpty(value))
        {
            resource = resource.WithEnvironment($"Mx__{key}", value);
        }
    }
    return resource;
}

// ── Application services ────────────────────────────────────────────────────
var api = builder.AddProject<Projects.FlowLedger_Api>("api")
    .WithReference(postgres)
    .WithReference(redis)
    .WaitFor(postgres)
    .WaitFor(redis);

WithMxConfig(api, builder.Configuration);

var web = builder.AddProject<Projects.FlowLedger_Web>("web")
    .WithReference(api)
    .WaitFor(api);

var worker = builder.AddProject<Projects.FlowLedger_Worker>("worker")
    .WithReference(postgres)
    .WithReference(redis)
    .WaitFor(postgres)
    .WaitFor(redis);

WithMxConfig(worker, builder.Configuration);

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
