namespace FlowLedger.Api.Tests;

/// <summary>
/// Shares the Development <see cref="FlowLedgerApiFactory"/> AND the Production
/// <see cref="FailClosedApiFactory"/> across every API integration test via a single
/// collection. Both WebApplicationFactory instances target the same Program entry point;
/// keeping them in ONE collection guarantees all such tests run sequentially and both
/// factories are created once and disposed once at the end of the session — avoiding the
/// shared host-builder cache cross-talk that causes spurious ObjectDisposedExceptions
/// when separate collections create/dispose these factories concurrently.
/// </summary>
[CollectionDefinition("ApiIntegration")]
public sealed class ApiIntegrationCollection
    : ICollectionFixture<FlowLedgerApiFactory>, ICollectionFixture<FailClosedApiFactory>;
