using FlowLedger.Infrastructure.Tests.Helpers;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// xUnit collection definition for all integration tests.
/// All tests sharing IntegrationTestFixture are grouped here so the Testcontainer
/// is created once per test collection rather than once per test class.
/// </summary>
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationTestFixture>
{
    // This class has no code; it's used solely to define the collection.
}
