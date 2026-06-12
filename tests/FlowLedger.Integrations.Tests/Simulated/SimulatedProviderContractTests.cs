using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using FlowLedger.Integrations.Tests.Contract;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Simulated;

/// <summary>
/// Runs the full provider contract suite against <see cref="SimulatedFinancialDataProvider"/>.
/// All tests in <see cref="FinancialProviderContractTests"/> are exercised with default options.
/// </summary>
public sealed class SimulatedProviderContractTests : FinancialProviderContractTests
{
    protected override IFinancialDataProvider CreateProvider() =>
        new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions()));
}
