using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace FlowLedger.Web.Auth;

public static class AuthSetup
{
    /// <summary>The tenant a signed-in user owns, resolved by the API from (issuer, subject).</summary>
    public const string TenantClaim = "fl_tenant";
    public const string UserClaim = "fl_user";
    public const string IdentityClientName = "flowledger-identity";

    public static OidcOptions AddFlowLedgerAuth(this WebApplicationBuilder builder)
    {
        var oidc = builder.Configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();

        // Identity-resolution client: API key only (no tenant yet; that is what we are asking for).
        builder.Services.AddHttpClient(IdentityClientName, c => c.BaseAddress = new Uri("https+http://api")).AddServiceDiscovery();

        if (!oidc.Enabled)
        {
            builder.Services.AddAuthentication();
            builder.Services.AddAuthorization();
            return oidc;
        }

        builder.Services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(o =>
            {
                o.Cookie.Name = "flowledger.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromDays(14);
                o.SlidingExpiration = true;
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.Authority = oidc.Authority;
                o.ClientId = oidc.ClientId;
                o.ClientSecret = oidc.ClientSecret;
                o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                o.MapInboundClaims = false;
                o.GetClaimsFromUserInfoEndpoint = true;
                o.SaveTokens = false;
                o.CallbackPath = "/signin-oidc";
                o.SignedOutCallbackPath = "/signout-callback-oidc";
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                foreach (var s in oidc.Scopes)
                {
                    o.Scope.Add(s);
                }

                o.TokenValidationParameters.NameClaimType = oidc.NameClaim;
                o.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var principal = ctx.Principal!;
                        var sub = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
                        var issuer = ctx.SecurityToken?.Issuer ?? oidc.Authority!;
                        if (string.IsNullOrEmpty(sub))
                        {
                            ctx.Fail("The identity provider returned no subject (sub) claim.");
                            return;
                        }

                        var apiKey = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<FlowLedger.Web.ApiClient.ApiAuthOptions>>().Value.Key;
                        var http = ctx.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(IdentityClientName);
                        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/users/resolve")
                        {
                            Content = JsonContent.Create(new
                            {
                                issuer,
                                subject = sub,
                                email = principal.FindFirstValue("email"),
                                emailVerified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase),
                                displayName = principal.FindFirstValue(oidc.NameClaim) ?? principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username"),
                            }),
                        };
                        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                        using var resp = await http.SendAsync(req, ctx.HttpContext.RequestAborted);
                        resp.EnsureSuccessStatusCode();
                        var user = await resp.Content.ReadFromJsonAsync<ResolveResponse>(cancellationToken: ctx.HttpContext.RequestAborted)
                                   ?? throw new InvalidOperationException("Empty user-resolution response.");

                        var id = (ClaimsIdentity)principal.Identity!;
                        id.AddClaim(new Claim(TenantClaim, user.TenantId.ToString()));
                        id.AddClaim(new Claim(UserClaim, user.UserId.ToString()));
                    },
                    OnRemoteFailure = ctx =>
                    {
                        ctx.Response.Redirect("/auth/failed");
                        ctx.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });

        builder.Services.AddAuthorization(o =>
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return oidc;
    }

    public static void MapFlowLedgerAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/login", (string? returnUrl) =>
                Results.Challenge(new AuthenticationProperties { RedirectUri = SafeLocal(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        // After linking a login the tenant claim in the cookie is stale: clear only the local cookie, then re-challenge
        // (the IdP session is still alive, so the user is not prompted again) to re-resolve identity -> tenant.
        app.MapGet("/auth/relogin", async (HttpContext ctx) =>
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Challenge(new AuthenticationProperties { RedirectUri = "/account/logins" },
                    [OpenIdConnectDefaults.AuthenticationScheme]);
            })
            .RequireAuthorization();

        app.MapGet("/auth/failed", () => Results.Text(
                "Sign-in failed. Try again at /auth/login. If it keeps failing, check the identity provider configuration.",
                statusCode: StatusCodes.Status401Unauthorized))
            .AllowAnonymous();

        // POST only (logout is state-changing); the layout renders a form with an antiforgery token.
        app.MapPost("/auth/logout", () =>
                Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
            .RequireAuthorization();
    }

    private static string SafeLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    private sealed record ResolveResponse(Guid UserId, Guid TenantId);
}
