using System.Runtime.CompilerServices;

// Expose internal types (MxApiClient, MxWebhookVerifier, internal ctor of MxFinancialDataProvider,
// Contracts, Mapping) to the integration test project for white-box contract/unit tests.
[assembly: InternalsVisibleTo("FlowLedger.Integrations.Tests")]
