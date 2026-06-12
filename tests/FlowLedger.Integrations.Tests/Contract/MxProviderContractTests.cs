using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Tests.Contract;

/// <summary>
/// Opt-in contract tests for the real MX HTTP provider.
/// All tests are individually skipped when <c>Mx__ApiKey</c> is not present.
///
/// To run against a live MX Sandbox:
///   set Mx__Enabled=true
///   set Mx__ApiKey=&lt;your-key&gt;
///   set Mx__ClientId=&lt;your-client-id&gt;
///   set Mx__BaseUrl=https://int-api.mx.com
///   set Mx__WebhookSecret=&lt;your-secret&gt;
///   dotnet test --filter "FullyQualifiedName~MxProviderContractTests"
///
/// NOTE: The real MX provider (FlowLedger.Integrations.Mx) is not yet implemented.
/// This placeholder will be completed in Milestone 7.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Provider", "MX")]
public sealed class MxProviderContractTests
{
    private const string SkipReason =
        "MX provider contract tests require Mx:Enabled=true and Mx:ApiKey. " +
        "Skipping — configure MX sandbox credentials to opt-in. " +
        "MxFinancialDataProvider is not yet implemented (Milestone 7).";

    [Fact(Skip = SkipReason)]
    public void Mx_provider_satisfies_contract_placeholder()
    {
        // TODO(milestone-7): Replace with a MxProviderContractTests : FinancialProviderContractTests
        // subclass once FlowLedger.Integrations.Mx is implemented.
        // Delete this placeholder test at that point.
    }
}
