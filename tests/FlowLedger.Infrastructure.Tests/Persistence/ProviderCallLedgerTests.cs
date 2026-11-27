using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.TestSupport;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class ProviderCallLedgerTests
{
    private readonly IntegrationTestFixture _fixture;

    public ProviderCallLedgerTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task Records_counts_and_summarises()
    {
        var ledger = new ProviderCallLedger(_fixture.ConnectionString);
        var provider = "TEST-" + Guid.NewGuid().ToString("N");
        var now = TestClock.Instant;

        await ledger.RecordAsync(new ProviderCallRecord(provider, "GET", "/users", 200, 12, 100, now));
        await ledger.RecordAsync(new ProviderCallRecord(provider, "GET", "/users", 200, 10, 90, now));
        await ledger.RecordAsync(new ProviderCallRecord(provider, "POST", "/members", 500, 30, null, now.AddDays(-400), "HttpRequestException"));

        (await ledger.CountSinceAsync(provider, now.AddHours(-1))).Should().Be(2);
        (await ledger.CountSinceAsync(provider, now.AddDays(-500))).Should().Be(3);

        var usage = await ledger.GetUsageAsync(provider, now);
        usage.CallsToday.Should().Be(2);
        usage.ByEndpoint.Should().ContainSingle(e => e.Path == "/users" && e.Calls == 2);
    }
}
