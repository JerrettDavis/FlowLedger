namespace FlowLedger.Api.Tests;

/// <summary>
/// Shares a single <see cref="FlowLedgerApiFactory"/> (one Postgres container,
/// one host) across all tests in the "ApiIntegration" collection.
/// </summary>
[CollectionDefinition("ApiIntegration")]
public sealed class ApiIntegrationCollection : ICollectionFixture<FlowLedgerApiFactory>;
