using FlowLedger.Worker;

var builder = Host.CreateApplicationBuilder(args);

// ── Service defaults (OpenTelemetry, health checks, service discovery) ──────
builder.AddServiceDefaults();

// ── Background workers ───────────────────────────────────────────────────────
builder.Services.AddHostedService<PlaceholderWorker>();

var host = builder.Build();
host.Run();
