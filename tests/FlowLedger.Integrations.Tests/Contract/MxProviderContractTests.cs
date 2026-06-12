using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Tests.Mx;

namespace FlowLedger.Integrations.Tests.Contract;

/// <summary>
/// Runs the FULL provider contract suite against the real <see cref="FlowLedger.Integrations.Mx.MxFinancialDataProvider"/>
/// wired to a WireMock.Net server that mirrors the MX Platform API. No real API key is needed.
///
/// This replaces the Milestone-7 skipped placeholder: the MX provider must satisfy every base
/// contract case exactly as the Simulated provider does.
/// </summary>
[Trait("Provider", "MX")]
public sealed class MxProviderContractTests : FinancialProviderContractTests, IClassFixture<MxWireMockFixture>
{
    private readonly MxWireMockFixture _fixture;

    public MxProviderContractTests(MxWireMockFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IFinancialDataProvider CreateProvider() => _fixture.CreateProvider();
}
