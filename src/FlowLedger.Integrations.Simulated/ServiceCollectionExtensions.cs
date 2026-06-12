using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Integrations.Simulated;

/// <summary>
/// DI registration extension for the Simulated financial data provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SimulatedFinancialDataProvider"/> as the
    /// <see cref="IFinancialDataProvider"/> singleton.
    ///
    /// Configuration is read from the <c>SimulatedProvider</c> section
    /// (see <see cref="SimulatedProviderOptions"/>).  All options are optional
    /// and default to a zero-latency, zero-failure, 6-month history configuration.
    /// </summary>
    public static IServiceCollection AddSimulatedProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SimulatedProviderOptions>(
            configuration.GetSection(SimulatedProviderOptions.SectionName));

        services.AddSingleton<IFinancialDataProvider, SimulatedFinancialDataProvider>();

        return services;
    }
}
