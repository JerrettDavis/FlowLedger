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

builder.Build().Run();
