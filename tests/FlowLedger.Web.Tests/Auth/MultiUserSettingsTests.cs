using FlowLedger.ServiceDefaults;
using FlowLedger.Web.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.Tests.Auth;

/// <summary>#135 / #145: the shared multi-user startup validation (sign-up mode, signed tenant assertion, household tenant id).</summary>
public sealed class MultiUserSettingsTests
{
    private const string GoodKey = "0123456789abcdef0123456789abcdef0123";
    private const string ApiKey = "api-key-abcdef0123456789abcdef0123456789";

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "FlowLedger.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IConfiguration Cfg(params (string Key, string? Value)[] values)
    {
        var d = new Dictionary<string, string?> { ["Api:Key"] = ApiKey };
        foreach (var (k, v) in values)
        {
            d[k] = v;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static readonly (string, string?) OidcOn1 = ("Auth:Oidc:Authority", "https://idp.test/");
    private static readonly (string, string?) OidcOn2 = ("Auth:Oidc:ClientId", "flowledger");
    private static readonly (string, string?) RandomTenant = ("Api:TenantId", "5f1c2a3b-0000-4000-8000-00000000abcd");
    private static readonly (string, string?) Signing = ("Api:TenantSigningKey", GoodKey);

    // ── #145 sign-up mode ─────────────────────────────────────────────────────────────

    [Fact]
    public void Unset_mode_defaults_to_InviteOnly() =>
        MultiUserSettings.ResolveSignUpMode(Cfg()).Should().Be(SignUpMode.InviteOnly);

    [Fact]
    public void LegacyOpen_restores_open_sign_up() =>
        MultiUserSettings.ResolveSignUpMode(Cfg((MultiUserSettings.SignUpLegacyOpenKey, "true"))).Should().Be(SignUpMode.Open);

    [Theory]
    [InlineData("Open", SignUpMode.Open)]
    [InlineData("invite-only", SignUpMode.InviteOnly, true)]
    [InlineData("InviteOnly", SignUpMode.InviteOnly)]
    [InlineData("closed", SignUpMode.Closed)]
    public void Explicit_mode_wins_over_legacy_flag(string raw, SignUpMode expected, bool invalid = false)
    {
        var cfg = Cfg((MultiUserSettings.SignUpModeKey, raw), (MultiUserSettings.SignUpLegacyOpenKey, "true"));
        if (invalid)
        {
            var act = () => MultiUserSettings.ResolveSignUpMode(cfg);
            act.Should().Throw<InvalidOperationException>().WithMessage("*Open, InviteOnly or Closed*");
            return;
        }

        MultiUserSettings.ResolveSignUpMode(cfg).Should().Be(expected);
    }

    [Theory]
    [InlineData(SignUpMode.Open, false, true)]
    [InlineData(SignUpMode.Open, true, true)]
    [InlineData(SignUpMode.InviteOnly, true, true)]
    [InlineData(SignUpMode.InviteOnly, false, false)]
    [InlineData(SignUpMode.Closed, true, false)]
    [InlineData(SignUpMode.Closed, false, false)]
    public void New_tenants_are_allowed_per_mode(SignUpMode mode, bool allowlist, bool expected)
    {
        MultiUserSettings.AllowsNewTenants(mode, allowlist).Should().Be(expected);
        new OidcOptions { SignUpMode = mode, AllowedEmails = allowlist ? ["a@x.com"] : [] }.AllowsNewTenants.Should().Be(expected);
    }

    [Fact]
    public void Allowlist_is_detected_from_indexed_and_scalar_config()
    {
        MultiUserSettings.HasAllowlist(Cfg()).Should().BeFalse();
        MultiUserSettings.HasAllowlist(Cfg(("Auth:Oidc:AllowedEmails:0", "a@x.com"))).Should().BeTrue();
        MultiUserSettings.HasAllowlist(Cfg(("Auth:Oidc:AllowedEmailDomains:0", "family.org"))).Should().BeTrue();
        MultiUserSettings.HasAllowlist(Cfg(("Auth:Oidc:AllowedEmails:0", " "))).Should().BeFalse();
    }

    [Fact]
    public void Open_sign_up_logs_a_warning_when_active()
    {
        var log = new CaptureLogger();

        MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant, Signing, (MultiUserSettings.SignUpLegacyOpenKey, "true")), new Env("Production"), log);

        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Open"));
    }

    [Fact]
    public void InviteOnly_without_an_allowlist_warns_that_nobody_new_can_join()
    {
        var log = new CaptureLogger();

        MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant, Signing), new Env("Production"), log);

        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("no NEW login"));
    }

    [Fact]
    public void Closed_or_invite_only_with_an_allowlist_is_quiet()
    {
        var log = new CaptureLogger();

        MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant, Signing, ("Auth:Oidc:AllowedEmails:0", "a@x.com")), new Env("Production"), log);

        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Trusting_unverified_email_warns()
    {
        var log = new CaptureLogger();

        MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant, Signing, ("Tenancy:TrustUnverifiedEmail", "true"), ("Auth:Oidc:AllowedEmails:0", "a@x.com")),
            new Env("Production"), log);

        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("TrustUnverifiedEmail"));
    }

    [Fact]
    public void Trusting_unverified_email_with_auto_link_is_refused_unless_the_issuer_is_pinned_or_acknowledged()
    {
        var risky = new[] { OidcOn1, OidcOn2, RandomTenant, Signing, ("Tenancy:TrustUnverifiedEmail", "true"), ("Tenancy:AutoLinkVerifiedEmail", "true") };

        var act = () => MultiUserSettings.Validate(Cfg(risky), new Env("Production"), new CaptureLogger());
        act.Should().Throw<InvalidOperationException>().WithMessage("*Tenancy:LegacyOwnerIssuer*Auth:SignUp:LegacyEmailTrust*");

        MultiUserSettings.Validate(Cfg([.. risky, ("Tenancy:LegacyOwnerIssuer", "https://idp.test/")]), new Env("Production"), new CaptureLogger());
        MultiUserSettings.Validate(Cfg([.. risky, (MultiUserSettings.LegacyEmailTrustKey, "true")]), new Env("Production"), new CaptureLogger());
        MultiUserSettings.Validate(Cfg(risky), new Env("Development"), new CaptureLogger());
    }

    // ── #135 signed tenant assertion ──────────────────────────────────────────────────

    [Fact]
    public void Oidc_in_production_requires_a_signing_key_by_default()
    {
        var act = () => MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant), new Env("Production"), new CaptureLogger());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Api:TenantSigningKey*Api:RequireSignedTenant*false*");
    }

    [Theory]
    [InlineData("short")]
    [InlineData(ApiKey)]
    public void A_weak_or_reused_signing_key_is_refused(string key)
    {
        var act = () => MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant, ("Api:TenantSigningKey", key)), new Env("Production"), new CaptureLogger());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Api:TenantSigningKey*");
    }

    [Fact]
    public void Oidc_with_a_strong_key_starts()
    {
        var act = () => MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant, Signing, ("Auth:Oidc:AllowedEmails:0", "a@x.com")), new Env("Production"), new CaptureLogger());

        act.Should().NotThrow();
    }

    [Fact]
    public void Development_and_non_oidc_hosts_do_not_require_a_key()
    {
        MultiUserSettings.RequireSignedTenant(Cfg(OidcOn1, OidcOn2), new Env("Development")).Should().BeFalse();
        MultiUserSettings.RequireSignedTenant(Cfg(), new Env("Production")).Should().BeFalse();
        MultiUserSettings.RequireSignedTenant(Cfg(OidcOn1, OidcOn2), new Env("Production")).Should().BeTrue();
        MultiUserSettings.Validate(Cfg(), new Env("Production"), new CaptureLogger());
    }

    [Fact]
    public void The_legacy_opt_out_starts_but_warns()
    {
        var log = new CaptureLogger();

        MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, RandomTenant, (MultiUserSettings.RequireSignedTenantKey, "false"), ("Auth:Oidc:AllowedEmails:0", "a@x.com")),
            new Env("Production"), log);

        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("RequireSignedTenant=false"));
    }

    [Fact]
    public void An_explicit_require_without_a_key_fails_even_without_oidc()
    {
        var act = () => MultiUserSettings.Validate(Cfg((MultiUserSettings.RequireSignedTenantKey, "true")), new Env("Production"), new CaptureLogger());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Api:TenantSigningKey*");
    }

    // ── #135 household tenant id ──────────────────────────────────────────────────────

    [Fact]
    public void Oidc_with_the_well_known_household_tenant_is_refused_in_production()
    {
        var act = () => MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, Signing), new Env("Production"), new CaptureLogger());

        act.Should().Throw<InvalidOperationException>().WithMessage("*well-known*Tenancy:AllowWellKnownLegacyTenantId*");
    }

    [Fact]
    public void An_existing_install_can_keep_the_well_known_tenant_explicitly_and_is_warned()
    {
        var log = new CaptureLogger();

        MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, Signing, (MultiUserSettings.AllowWellKnownTenantKey, "true"), ("Auth:Oidc:AllowedEmails:0", "a@x.com")),
            new Env("Production"), log);

        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("publicly known"));
    }

    [Theory]
    [InlineData("Tenancy:LegacyTenantId")]
    [InlineData("Api:TenantId")]
    public void A_configured_random_tenant_id_satisfies_the_guard(string key)
    {
        var act = () => MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, Signing, (key, "5f1c2a3b-0000-4000-8000-00000000abcd"), ("Auth:Oidc:AllowedEmails:0", "a@x.com")),
            new Env("Production"), new CaptureLogger());

        act.Should().NotThrow();
    }

    [Fact]
    public void The_household_tenant_guard_does_not_apply_in_development_or_without_oidc()
    {
        MultiUserSettings.Validate(Cfg(OidcOn1, OidcOn2, Signing), new Env("Development"), new CaptureLogger());
        MultiUserSettings.Validate(Cfg(), new Env("Production"), new CaptureLogger());
    }
}
