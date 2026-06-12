var builder = DistributedApplication.CreateBuilder(args);

// ── Infrastructure resources ────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(isReadOnly: false)
    .AddDatabase("flowledger");

var redis = builder.AddRedis("redis")
    .WithDataVolume(isReadOnly: false);

// ── Application services ────────────────────────────────────────────────────
var api = builder.AddProject<Projects.FlowLedger_Api>("api")
    .WithReference(postgres)
    .WithReference(redis)
    .WaitFor(postgres)
    .WaitFor(redis);

var web = builder.AddProject<Projects.FlowLedger_Web>("web")
    .WithReference(api)
    .WaitFor(api);

builder.AddProject<Projects.FlowLedger_Worker>("worker")
    .WithReference(postgres)
    .WithReference(redis)
    .WaitFor(postgres)
    .WaitFor(redis);

builder.Build().Run();
