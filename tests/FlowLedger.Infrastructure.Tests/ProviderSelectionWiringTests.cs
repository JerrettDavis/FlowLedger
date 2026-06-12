using FluentAssertions;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.Integrations.Simulated;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests;

/// <summary>
/// Verifies that the financial-data provider is selected by configuration ALONE:
///   Mx:Enabled=true  → MxFinancialDataProvider
///   Mx:Enabled=false → SimulatedFinancialDataProvider
/// and that Mx:Enabled=true with a missing key fails fast via the options validator.
///
/// These tests resolve only the provider/options (never the DbContext), so no database is needed.
/// </summary>
public sealed class ProviderSelectionWiringTests
{
    private static ServiceProvider BuildContainer(Dictionary<string, string?> settings)
    {
        // A connection string keeps AddInfrastructure's DbContext registration happy; it is never
        // resolved in these tests.
        settings["ConnectionStrings:flowledger"] = "Host=localhost;Database=unused;Username=u;Password=p";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Mx_disabled_resolves_simulated_provider()
    {
        using var sp = BuildContainer(new Dictionary<string, string?>
        {
            ["Mx:Enabled"] = "false",
        });

        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IFinancialDataProvider>();

        provider.Should().BeOfType<SimulatedFinancialDataProvider>();
        provider.ProviderName.Should().Be("Simulated");
    }

    [Fact]
    public void Mx_enabled_with_full_config_resolves_mx_provider()
    {
        using var sp = BuildContainer(new Dictionary<string, string?>
        {
            ["Mx:Enabled"] = "true",
            ["Mx:ApiKey"] = "test-key",
            ["Mx:ClientId"] = "test-client",
            ["Mx:BaseUrl"] = "https://int-api.mx.com",
            ["Mx:WebhookSecret"] = "test-secret",
        });

        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IFinancialDataProvider>();

        provider.Should().BeOfType<MxFinancialDataProvider>();
        provider.ProviderName.Should().Be("MX");
    }

    [Fact]
    public void Mx_enabled_without_key_fails_fast_on_options_validation()
    {
        using var sp = BuildContainer(new Dictionary<string, string?>
        {
            ["Mx:Enabled"] = "true",
            // No ApiKey / ClientId / BaseUrl / WebhookSecret — must fail validation.
        });

        var act = () => sp.GetRequiredService<IOptions<FinancialProviderOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("ApiKey");
    }
}
