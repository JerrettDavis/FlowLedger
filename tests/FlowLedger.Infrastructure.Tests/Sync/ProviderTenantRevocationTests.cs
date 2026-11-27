using FlowLedger.Application.Features.Users;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>#189: erasing a tenant asks each connector whose API supports it to revoke; best effort, logged, never blocking.</summary>
public sealed class ProviderTenantRevocationTests
{
    private abstract class FakeProvider(string name) : IFinancialDataProvider
    {
        public string ProviderName => name;
        public ProviderCapabilities Capabilities => throw new NotSupportedException();
        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConnectionStatus> GetConnectionStatusAsync(string memberProviderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string memberProviderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransactionPage> GetTransactionsAsync(string providerAccountId, SyncCursor cursor, int pageSize = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> rawBody, string signatureHeader, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Plain(string name) : FakeProvider(name);

    private sealed class Revoking(string name, Func<ProviderRevocationOutcome> act) : FakeProvider(name), IProviderTenantRevoker
    {
        public List<Guid> Tenants { get; } = [];

        public Task<ProviderRevocationOutcome> RevokeTenantAsync(TenantId tenantId, CancellationToken cancellationToken = default)
        {
            Tenants.Add(tenantId.Value);
            return Task.FromResult(act());
        }
    }

    private sealed class Registry(params (string Id, bool Shared, IFinancialDataProvider Provider)[] items) : IConnectorRegistry
    {
        public IReadOnlyList<ConnectorDescriptor> Enabled { get; } = items.Select(i => new ConnectorDescriptor(new ConnectorId(i.Id), i.Id, "test", i.Shared)).ToList();

        public bool IsEnabled(ConnectorId connectorId) => Enabled.Any(c => c.Id == connectorId);

        public IFinancialDataProvider Resolve(ConnectorId connectorId) => items.Single(i => i.Id == connectorId.Value).Provider;
    }

    private static ProviderTenantRevocation Sut(IConnectorRegistry registry) => new(registry, NullLogger<ProviderTenantRevocation>.Instance);

    [Fact]
    public async Task Each_connector_is_revoked_reported_as_unsupported_or_skipped_as_shared()
    {
        var tenant = Guid.NewGuid();
        var mx = new Revoking("MX", () => ProviderRevocationOutcome.Revoked);
        var quiet = new Revoking("Quiltt", () => ProviderRevocationOutcome.NothingToRevoke);
        var registry = new Registry(("MX", false, mx), ("Quiltt", false, quiet), ("SimpleFIN", false, new Plain("SimpleFIN")), ("Shared", true, new Revoking("Shared", () => throw new InvalidOperationException("must not be called"))));

        var results = await Sut(registry).RevokeAllAsync(tenant);

        results.Select(r => (r.Connector, r.Status)).Should().Equal(
            ("MX", ProviderRevocationStatus.Revoked),
            ("Quiltt", ProviderRevocationStatus.NothingToRevoke),
            ("SimpleFIN", ProviderRevocationStatus.NotSupported),
            ("Shared", ProviderRevocationStatus.NothingToRevoke));
        mx.Tenants.Should().Equal(tenant);
    }

    [Fact]
    public async Task A_provider_that_throws_is_reported_failed_and_the_others_still_run()
    {
        var after = new Revoking("Quiltt", () => ProviderRevocationOutcome.Revoked);
        var registry = new Registry(("MX", false, new Revoking("MX", () => throw new HttpRequestException("MX is down"))), ("Quiltt", false, after));

        var results = await Sut(registry).RevokeAllAsync(Guid.NewGuid());

        results.Select(r => r.Status).Should().Equal(ProviderRevocationStatus.Failed, ProviderRevocationStatus.Revoked);
        after.Tenants.Should().HaveCount(1);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var registry = new Registry(("MX", false, new Revoking("MX", () => throw new OperationCanceledException())));

        var act = () => Sut(registry).RevokeAllAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
