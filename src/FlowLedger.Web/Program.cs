using FlowLedger.Web.Client.Pages;
using FlowLedger.Web.Components;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Service defaults (OpenTelemetry, health checks, service discovery) ──────
builder.AddServiceDefaults();

// ── Razor + Blazor ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// ── MudBlazor ───────────────────────────────────────────────────────────────
builder.Services.AddMudServices();

var app = builder.Build();

// ── Middleware ───────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

// ── Aspire health + liveness endpoints ──────────────────────────────────────
app.MapDefaultEndpoints();

// ── Static assets + Razor components ─────────────────────────────────────────
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FlowLedger.Web.Client._Imports).Assembly);

app.Run();
