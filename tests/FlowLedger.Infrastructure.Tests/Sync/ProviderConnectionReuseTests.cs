using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// Guards the cost-critical invariant: a provider member is provisioned ONCE per tenant and
/// reused by every later sync (including from brand-new service instances / process restarts).
/// With real MX, re-provisioning would create a billable orphan user+member on every sync.
/// </summary>
[Collection("Integration")]
public sealed class ProviderConnectionReuseTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public ProviderConnectionReuseTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Repeated_syncs_across_fresh_service_instances_provision_exactly_once()
    {
        var tenant = TestTenantContext.New();
        var inner = new SimulatedFinancialDataProvider(Options.Create(new SimulatedProviderOptions()));
        var counting = new CountingProvider(inner);

        for (var i = 0; i < 3; i++)
        {
            // New DbContext + new service each time == a process restart between syncs.
            await using var db = _fixture.CreateDbContext(tenant);
            var svc = new FinancialSyncService(
                counting, db, tenant, new EfSyncCursorStore(db, tenant),
                NullLogger<FinancialSyncService>.Instance);
            await svc.SyncAsync();
        }

        counting.BeginConnectionCalls.Should().Be(1, "the member must be provisioned once and reused");

        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.ProviderConnections.CountAsync()).Should().Be(1);
    }

    [DockerFact]
    public async Task Unattended_sync_never_provisions_a_real_provider_connection()
    {
        var tenant = TestTenantContext.New();
        var inner = new SimulatedFinancialDataProvider(Options.Create(new SimulatedProviderOptions()));
        var realLike = new CountingProvider(inner, nameOverride: "MX", traits: ConnectorTraits.BillablePerCall);

        await using var db = _fixture.CreateDbContext(tenant);
        var svc = new FinancialSyncService(
            realLike, db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance);

        var act = () => svc.SyncAsync();

        await act.Should().ThrowAsync<NeedsUserActionProviderException>();
        realLike.BeginConnectionCalls.Should().Be(0, "a scheduler/webhook sync must never create billable provider users or members");
    }

    private sealed class CountingProvider(IFinancialDataProvider inner, string? nameOverride = null, ConnectorTraits? traits = null) : IFinancialDataProvider
    {
        public int BeginConnectionCalls { get; private set; }
        public string ProviderName => nameOverride ?? inner.ProviderName;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ConnectorTraits Traits => traits ?? inner.Traits;

        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId t, CancellationToken ct = default)
        {
            BeginConnectionCalls++;
            return inner.BeginConnectionAsync(t, ct);
        }

        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken ct = default)
            => inner.GetConnectionStatusAsync(m, ct);
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken ct = default)
            => inner.GetAccountsAsync(m, ct);
        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
            => inner.GetTransactionsAsync(a, c, p, ct);
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default)
            => inner.VerifyWebhookAsync(b, s, ct);
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => inner.ParseWebhookAsync(b, ct);
    }
}
