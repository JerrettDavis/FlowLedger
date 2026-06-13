using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components;
using FlowLedger.Web.Services;
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

// ── Theme ────────────────────────────────────────────────────────────────────
// Scoped so each circuit (browser tab) gets its own independent theme state.
builder.Services.AddScoped<ThemeService>();

// ── API auth options (Api:Key + Api:TenantId from config / env vars) ─────────
builder.Services.Configure<ApiAuthOptions>(
    builder.Configuration.GetSection(ApiAuthOptions.SectionName));

// ── FlowLedger API typed client ───────────────────────────────────────────────
// ApiAuthHeaderHandler adds X-Api-Key and X-Tenant-Id to every outbound request.
// "api" matches the Aspire resource name in AppHost.cs and the Docker Compose
// service-discovery env var (services__api__http__0).
builder.Services.AddTransient<ApiAuthHeaderHandler>();

builder.Services.AddHttpClient<FlowLedgerApiClient>(client =>
{
    client.BaseAddress = new Uri("https+http://api");
})
.AddHttpMessageHandler<ApiAuthHeaderHandler>()
.AddServiceDiscovery();

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
