using FlowLedger.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// ── Service defaults (OpenTelemetry, health checks, service discovery) ──────
builder.AddServiceDefaults();

// ── OpenAPI ─────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

var app = builder.Build();

// ── Middleware ───────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// ── Aspire health + liveness endpoints ──────────────────────────────────────
app.MapDefaultEndpoints();

// ── Feature endpoint groups ──────────────────────────────────────────────────
app.MapStatusEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
