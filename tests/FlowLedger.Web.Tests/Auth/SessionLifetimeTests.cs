using System.Security.Claims;
using FlowLedger.Web.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FlowLedger.Web.Tests.Auth;

/// <summary>#159: absolute session cap, __Host- cookie name and the revocation bound.</summary>
public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static ClaimsPrincipal Signed(DateTimeOffset? started)
    {
        var claims = new List<Claim> { new(AuthSetup.TenantClaim, Tenant.ToString()), new(AuthSetup.UserClaim, User.ToString()) };
        if (started is { } s)
        {
            claims.Add(new Claim(AuthSetup.SessionStartClaim, s.ToUnixTimeSeconds().ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie"));
    }

    // ── the cap decision ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(45, true)]
    public void The_cap_counts_from_sign_in_not_from_the_last_activity(int daysSinceSignIn, bool expired) =>
        AuthSetup.IsPastAbsoluteLifetime(Signed(Start), Start.AddDays(daysSinceSignIn), 30).Should().Be(expired);

    [Fact]
    public void A_non_positive_cap_disables_the_limit() =>
        AuthSetup.IsPastAbsoluteLifetime(Signed(Start), Start.AddDays(4000), 0).Should().BeFalse();

    [Fact]
    public void A_cookie_issued_before_the_cap_existed_is_unstamped_not_expired() =>
        AuthSetup.IsPastAbsoluteLifetime(Signed(null), Start, 30).Should().BeNull();

    [Fact]
    public void The_default_cap_is_30_days() => AuthSetup.DefaultSessionAbsoluteMaxDays.Should().Be(30);

    // ── circuits ──────────────────────────────────────────────────────────────────────

    private static Func<Guid, CancellationToken, Task<IReadOnlyCollection<Guid>?>> Linked() =>
        (_, _) => Task.FromResult<IReadOnlyCollection<Guid>?>([User]);

    [Fact]
    public async Task An_open_circuit_past_the_cap_ends_even_when_the_login_is_still_linked() =>
        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(Start), Linked(), default, 30, Start.AddDays(31))).Should().BeFalse();

    [Fact]
    public async Task An_open_circuit_within_the_cap_or_unstamped_stays_open()
    {
        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(Start), Linked(), default, 30, Start.AddDays(2))).Should().BeTrue();
        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(null), Linked(), default, 30, Start.AddDays(400))).Should().BeTrue();
    }

    [Fact]
    public async Task Unlinking_a_login_ends_its_circuit_at_the_next_revalidation_and_that_is_at_most_five_minutes_away()
    {
        TenantMappingValidator.Interval.Should().BeLessOrEqualTo(TimeSpan.FromMinutes(5));
        var unlinked = Task.FromResult<IReadOnlyCollection<Guid>?>([Guid.NewGuid()]);

        (await TenantRevalidatingAuthenticationStateProvider.IsStillValidAsync(Signed(Start), (_, _) => unlinked, default, 30, Start)).Should().BeFalse();
    }

    // ── the cookie validator ──────────────────────────────────────────────────────────

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingAuth : IAuthenticationService
    {
        public int SignOuts { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOuts++;
            return Task.CompletedTask;
        }
    }

    // The mapping re-check asks the API which logins sit on the tenant; answer: this one still does.
    private sealed class LinkedFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Stub()) { BaseAddress = new Uri("http://api.test") };

        private sealed class Stub : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new[] { new { userId = User } }),
                });
        }
    }

    private static (CookieValidatePrincipalContext Ctx, RecordingAuth Auth) Context(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var auth = new RecordingAuth();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(auth)
            .AddSingleton<TimeProvider>(new FakeTime(now))
            .AddSingleton<IOptions<FlowLedger.Web.ApiClient.ApiAuthOptions>>(Options.Create(new FlowLedger.Web.ApiClient.ApiAuthOptions { Key = "k" }))
            .AddSingleton<IHttpClientFactory>(new LinkedFactory())
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), scheme.Name);
        return (new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), ticket), auth);
    }

    [Fact]
    public async Task A_cookie_past_the_cap_is_rejected_and_signed_out()
    {
        var (ctx, auth) = Context(Signed(Start), Start.AddDays(31));

        await AuthSetup.ValidateSessionAsync(ctx, 30);

        ctx.Principal.Should().BeNull("RejectPrincipal clears it");
        auth.SignOuts.Should().Be(1);
    }

    [Fact]
    public async Task A_cookie_without_a_start_stamp_is_stamped_and_renewed_instead_of_signing_everyone_out()
    {
        var (ctx, auth) = Context(Signed(null), Start);

        await AuthSetup.ValidateSessionAsync(ctx, 30);

        auth.SignOuts.Should().Be(0);
        ctx.ShouldRenew.Should().BeTrue();
        ctx.Principal!.FindFirst(AuthSetup.SessionStartClaim)!.Value.Should().Be(Start.ToUnixTimeSeconds().ToString());
    }

    // ── cookie name and attributes ────────────────────────────────────────────────────

    private static CookieAuthenticationOptions CookieOptions(string environment, params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Oidc:Authority"] = "https://idp.test/",
            ["Auth:Oidc:ClientId"] = "flowledger",
            ["Api:Key"] = "k",
        }.Concat(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))));
        builder.AddFlowLedgerAuth();
        using var app = builder.Build();
        return app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Outside_development_the_cookie_is_a_host_prefixed_secure_http_only_path_root_cookie()
    {
        var o = CookieOptions(Environments.Production);

        o.Cookie.Name.Should().Be("__Host-flowledger.auth");
        o.Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.Always);
        o.Cookie.HttpOnly.Should().BeTrue();
        o.Cookie.SameSite.Should().Be(SameSiteMode.Lax);
        o.Cookie.Path.Should().Be("/");
        o.Cookie.Domain.Should().BeNull();
    }

    [Fact]
    public void Development_and_the_legacy_flag_keep_the_old_cookie_name()
    {
        CookieOptions(Environments.Development).Cookie.Name.Should().Be("flowledger.auth");
        CookieOptions(Environments.Production, ("Auth:Session:LegacyCookieName", "true")).Cookie.Name.Should().Be("flowledger.auth");
    }
}
