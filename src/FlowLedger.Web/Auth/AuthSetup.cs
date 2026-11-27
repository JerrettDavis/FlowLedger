using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
        if (oidc.Enabled)
        {
            oidc.SignUpMode = FlowLedger.ServiceDefaults.MultiUserSettings.ResolveSignUpMode(builder.Configuration);
        }

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
                // Secure-only outside Development (remote access must go through a TLS proxy); plain-http localhost dev still works.
                o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromDays(14);
                o.SlidingExpiration = true;
                // Re-check login -> tenant every few minutes so an unlinked/moved login loses the old tenant's data.
                o.Events.OnValidatePrincipal = TenantMappingValidator.ValidateAsync;
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

                        if (!oidc.IsEmailAllowed(principal.FindFirstValue("email"),
                                string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase)))
                        {
                            ctx.Fail("This account is not on the FlowLedger invite list.");
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
                                // #145: an unknown login only gets a new tenant when the sign-up mode allows it (Open, or InviteOnly + allowlist hit).
                                allowProvisioning = oidc.AllowsNewTenants,
                            }),
                        };
                        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                        using var resp = await http.SendAsync(req, ctx.HttpContext.RequestAborted);
                        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            ctx.Fail("Sign-up is closed: this account is not registered and is not on the FlowLedger invite list.");
                            return;
                        }

                        resp.EnsureSuccessStatusCode();
                        var user = await resp.Content.ReadFromJsonAsync<ResolveResponse>(cancellationToken: ctx.HttpContext.RequestAborted)
                                   ?? throw new InvalidOperationException("Empty user-resolution response.");

                        var id = (ClaimsIdentity)principal.Identity!;
                        id.AddClaim(new Claim(TenantClaim, user.TenantId.ToString()));
                        id.AddClaim(new Claim(UserClaim, user.UserId.ToString()));
                        if (user.IsHousehold)
                        {
                            id.AddClaim(new Claim(HouseholdAccess.Claim, "true"));
                        }

                        // Just resolved: the first periodic mapping re-check is due one interval from now.
                        ctx.Properties!.Items[TenantMappingValidator.CheckedItem] =
                            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>Config key that deliberately allows running WITHOUT sign-in outside Development (trusted single-user LAN only).</summary>
    public const string AllowAnonymousKey = "Auth:AllowAnonymous";

    /// <summary>
    /// Without OIDC the Web app has no sign-in at all and acts as the household tenant for whoever reaches it.
    /// That is fine for local development, but outside Development it must be an explicit choice.
    /// </summary>
    public static void EnsureSignInConfigured(OidcOptions oidc, IHostEnvironment env, IConfiguration cfg)
    {
        if (oidc.Enabled || env.IsDevelopment() || cfg.GetValue(AllowAnonymousKey, false))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Sign-in is not configured (Auth:Oidc:Authority / Auth:Oidc:ClientId are empty) and the environment is '{env.EnvironmentName}'. " +
            "Without OIDC anyone who can reach the Web UI sees and changes the household's data. Configure Auth__Oidc__* " +
            "(see docs/self-hosting/multi-user-auth.md) or, for a trusted single-user network only, set Auth__AllowAnonymous=true.");
    }

    /// <summary>Config key for the directory that holds the persisted ASP.NET Data Protection key ring.</summary>
    public const string DataProtectionKeyPathKey = "DataProtection:KeyPath";

    /// <summary>
    /// Persists the Data Protection key ring so session cookies and antiforgery tokens survive restarts and
    /// are shared by replicas. When OIDC sign-in is on outside Development a key path is mandatory, because an
    /// ephemeral ring silently signs every user out on each container recreate.
    /// </summary>
    public static void ConfigureDataProtection(IServiceCollection services, OidcOptions oidc, IHostEnvironment env, IConfiguration cfg)
    {
        var keyPath = cfg[DataProtectionKeyPathKey];
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            if (oidc.Enabled && !env.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{DataProtectionKeyPathKey} is not set. Without a persisted key ring every restart invalidates all sessions " +
                    "and antiforgery tokens. Mount a volume and set DataProtection__KeyPath (see docs/self-hosting/production.md).");
            }

            return;
        }

        services.AddDataProtection()
            .SetApplicationName("FlowLedger.Web")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
    }

    public static void MapFlowLedgerAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/login", (string? returnUrl) =>
                Results.Challenge(new AuthenticationProperties { RedirectUri = SafeLocal(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        // After linking a login the tenant claim in the cookie is stale: clear only the local cookie, then re-challenge
        // (the IdP session is still alive, so the user is not prompted again) to re-resolve identity -> tenant.
        app.MapGet("/auth/relogin", async (HttpContext ctx, string? returnUrl) =>
            {
                // State-changing GET: refuse requests initiated from another site (login CSRF). Browsers always send Sec-Fetch-Site.
                var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
                if (site.Length > 0 && site is not ("same-origin" or "none"))
                {
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }

                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Challenge(new AuthenticationProperties { RedirectUri = string.IsNullOrEmpty(returnUrl) ? "/account/logins" : SafeLocal(returnUrl) },
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
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal) && !url.Any(char.IsControl) ? url : "/";

    private sealed record ResolveResponse(Guid UserId, Guid TenantId, bool IsHousehold = false);
}
