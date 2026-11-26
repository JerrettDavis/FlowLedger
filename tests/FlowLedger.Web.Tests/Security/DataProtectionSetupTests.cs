using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowLedger.Web.Tests.Security;

public sealed class DataProtectionSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-dp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static IConfiguration Config(params (string, string?)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Item1, v => v.Item2)).Build();

    private static ServiceProvider Build(IConfiguration config, string env)
    {
        var services = new ServiceCollection();
        services.AddFlowLedgerDataProtection(config, env);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Payload_protected_before_a_restart_unprotects_after_it_when_the_key_ring_is_persisted()
    {
        var config = Config(("DataProtection:KeysPath", _dir));

        string cookie;
        using (var first = Build(config, Environments.Production))
        {
            cookie = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("user-1");
        }

        // A brand-new container/process with the same key directory (docker compose up --force-recreate).
        using var second = Build(config, Environments.Production);
        second.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Unprotect(cookie).Should().Be("user-1");
        Directory.GetFiles(_dir, "key-*.xml").Should().NotBeEmpty();
    }

    [Fact]
    public void Required_outside_development_is_enforced_only_when_asked_for()
    {
        var unconfigured = Config();
        var act = () => Build(Config(("DataProtection:Required", "true")), Environments.Production);
        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:KeysPath*");

        // Default stays permissive (warns) so existing deployments keep starting; Development never throws.
        Build(unconfigured, Environments.Production).Dispose();
        Build(Config(("DataProtection:Required", "true")), Environments.Development).Dispose();
    }
}
