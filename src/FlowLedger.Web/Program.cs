using System.Globalization;
using FlowLedger.ServiceDefaults;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Auth;
using FlowLedger.Web.Components;
using FlowLedger.Web.Security;
using FlowLedger.Web.Services;
using MudBlazor.Services;

// Fallback culture for threads outside a request/circuit. Display currency and locale come from the per-user
// DisplaySettings cookie (default USD / en-US) via UseFlowLedgerDisplayCulture and DisplayCultureCircuitHandler.
var usCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = usCulture;
CultureInfo.DefaultThreadCurrentUICulture = usCulture;

var builder = WebApplication.CreateBuilder(args);

// ── Service defaults (OpenTelemetry, health checks, service discovery) ──────
builder.AddServiceDefaults();
builder.Services.AddFlowLedgerDataProtection(builder.Configuration, builder.Environment.EnvironmentName); // persisted key ring (DataProtection:KeysPath)

// ── Authentication (provider-agnostic OIDC; disabled when Auth:Oidc:Authority is empty) ──
var oidc = builder.AddFlowLedgerAuth();
AuthSetup.EnsureSignInConfigured(oidc, builder.Environment, builder.Configuration); // refuse to run open outside Development
builder.Services.AddSingleton(oidc);
AuthSetup.ConfigureDataProtection(builder.Services, oidc, builder.Environment, builder.Configuration);

// ── Razor + Blazor ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
if (oidc.Enabled)
{
    // #159: re-check the login -> tenant mapping inside open circuits too (the cookie validator only sees HTTP requests). Registered after
    // AddInteractiveServerComponents so it replaces the default ServerAuthenticationStateProvider.
    builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, FlowLedger.Web.Auth.TenantRevalidatingAuthenticationStateProvider>();
}

// ── MudBlazor ───────────────────────────────────────────────────────────────
builder.Services.AddMudServices();

// ── Theme ────────────────────────────────────────────────────────────────────
// Scoped so each circuit (browser tab) gets its own independent theme state.
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<SyncCoordinator>();

// -- Display currency + locale (display-only, no FX) --
builder.Services.AddScoped<DisplaySettingsService>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, DisplayCultureCircuitHandler>();

// ── Connection repair (provider-pluggable Reconnect; add a handler per provider) ──
builder.Services.AddScoped<FlowLedger.Web.Connections.IConnectionRepairHandler, FlowLedger.Web.Connections.QuilttRepairHandler>();
builder.Services.AddScoped<FlowLedger.Web.Connections.ConnectionRepairService>();

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

// Opt-in: behind a TLS-terminating reverse proxy, honour X-Forwarded-Proto/For (and Host when AllowedHosts is restricted) so the OIDC
// redirect_uri is https. Requires ForwardedHeaders:KnownProxies / KnownNetworks: startup fails if enabled without them.
var trustForwardedHeaders = builder.AddFlowLedgerForwardedHeaders();

var app = builder.Build();

// #135/#145/#159: fail closed (or warn) for unsafe multi-user settings (signed tenant assertion, sign-up policy, household tenant id).
MultiUserSettings.Validate(app.Configuration, app.Environment, app.Logger);

app.UseFlowLedgerForwardedHeaders(trustForwardedHeaders);

// ── Middleware ───────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Security headers on every response; full CSP outside Development (hot reload injects scripts).
app.UseFlowLedgerSecurityHeaders(enableCsp: !app.Environment.IsDevelopment());
app.UseFlowLedgerDisplayCulture();

app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
if (!oidc.Enabled)
{
    StartupLog.OidcNotConfigured(app.Logger);
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
    .AddInteractiveServerRenderMode();
if (oidc.Enabled)
{
    razor.RequireAuthorization();
}

app.Run();
