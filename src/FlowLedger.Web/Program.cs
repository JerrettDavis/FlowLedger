using System.Globalization;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Auth;
using FlowLedger.Web.Components;
using FlowLedger.Web.Security;
using FlowLedger.Web.Services;
using MudBlazor.Services;

// Ensure monetary formatting always uses en-US ($ symbol) regardless of host culture.
var usCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = usCulture;
CultureInfo.DefaultThreadCurrentUICulture = usCulture;

var builder = WebApplication.CreateBuilder(args);

// ── Service defaults (OpenTelemetry, health checks, service discovery) ──────
builder.AddServiceDefaults();

// ── Authentication (provider-agnostic OIDC; disabled when Auth:Oidc:Authority is empty) ──
var oidc = builder.AddFlowLedgerAuth();
AuthSetup.EnsureSignInConfigured(oidc, builder.Environment, builder.Configuration); // refuse to run open outside Development
builder.Services.AddSingleton(oidc);

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

// Security headers on every response; full CSP outside Development (hot reload / WASM debugging inject scripts).
app.UseFlowLedgerSecurityHeaders(enableCsp: !app.Environment.IsDevelopment());

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
if (!oidc.Enabled)
{
    app.Logger.LogWarning("Auth:Oidc is not configured: running in single-household mode WITHOUT sign-in. Configure Auth:Oidc before exposing this app.");
}
else
{
    app.MapFlowLedgerAuthEndpoints();
}

// ── Aspire health + liveness endpoints ──────────────────────────────────────
app.MapDefaultEndpoints();

// ── Static assets + Razor components ─────────────────────────────────────────
app.MapStaticAssets();
var razor = app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FlowLedger.Web.Client._Imports).Assembly);
if (oidc.Enabled)
{
    razor.RequireAuthorization();
}

app.Run();
