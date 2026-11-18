using FlowLedger.Web.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FlowLedger.Web.Tests.Auth;

public sealed class SignInRequiredTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "FlowLedger.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Cfg(string? allowAnonymous = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthSetup.AllowAnonymousKey] = allowAnonymous,
        }).Build();

    private static readonly OidcOptions Off = new();
    private static readonly OidcOptions On = new() { Authority = "https://idp.test/", ClientId = "fl" };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_development_without_oidc_refuses_to_start(string env)
    {
        var act = () => AuthSetup.EnsureSignInConfigured(Off, new Env(env), Cfg());
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth__AllowAnonymous*");
    }

    [Fact]
    public void Development_without_oidc_is_allowed() =>
        AuthSetup.EnsureSignInConfigured(Off, new Env("Development"), Cfg());

    [Fact]
    public void Explicit_opt_out_is_allowed() =>
        AuthSetup.EnsureSignInConfigured(Off, new Env("Production"), Cfg("true"));

    [Fact]
    public void Production_with_oidc_is_allowed() =>
        AuthSetup.EnsureSignInConfigured(On, new Env("Production"), Cfg());
}
