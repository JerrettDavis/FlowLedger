namespace FlowLedger.Api.Tests;

/// <summary>Own Postgres container so a test can stop the database without disturbing the shared factory.</summary>
public sealed class ReadinessApiFactory : FlowLedgerApiFactory
{
    public Task StopDatabaseAsync() => StopPostgresAsync();
}
