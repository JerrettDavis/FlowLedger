using FlowLedger.Web.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FlowLedger.Web.Tests.Auth;

public sealed class DataProtectionKeyRingTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "FlowLedger.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Cfg(string? keyPath) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthSetup.DataProtectionKeyPathKey] = keyPath,
        }).Build();

    private static readonly OidcOptions On = new() { Authority = "https://idp.test/", ClientId = "fl" };

    [Fact]
    public void Oidc_in_production_without_a_key_path_refuses_to_start()
    {
        var act = () => AuthSetup.ConfigureDataProtection(new ServiceCollection(), On, new Env("Production"), Cfg(null));
        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:KeyPath*");
    }

    [Fact]
    public void Development_or_no_oidc_without_a_key_path_is_allowed()
    {
        AuthSetup.ConfigureDataProtection(new ServiceCollection(), On, new Env("Development"), Cfg(null));
        AuthSetup.ConfigureDataProtection(new ServiceCollection(), new OidcOptions(), new Env("Production"), Cfg(null));
    }

    [Fact]
    public void Protected_payloads_survive_a_new_service_provider_when_a_key_path_is_set()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fl-dp-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Protect()
            {
                var services = new ServiceCollection();
                AuthSetup.ConfigureDataProtection(services, On, new Env("Production"), Cfg(dir));
                using var sp = services.BuildServiceProvider();
                return sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("t").Protect("secret");
            }

            string Unprotect(string payload)
            {
                var services = new ServiceCollection();
                AuthSetup.ConfigureDataProtection(services, On, new Env("Production"), Cfg(dir));
                using var sp = services.BuildServiceProvider();
                return sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("t").Unprotect(payload);
            }

            var payload = Protect();
            Unprotect(payload).Should().Be("secret"); // a "restarted" host reads the same persisted ring
            Directory.GetFiles(dir, "key-*.xml").Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
