using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using FlowLedger.TestSupport.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowLedger.Api.Tests;

/// <summary>
/// Host whose sync service is a fake connector that throws whatever <see cref="Failure"/> says (or succeeds when null) and whose
/// connection store reports <see cref="Connections"/>. Proves the HTTP layer depends only on the connector-neutral contract.
/// </summary>
public sealed class ProviderFailureApiFactory : FlowLedgerApiFactory
{
    /// <summary>The exception the fake connector throws on the next sync; null means succeed.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The connections the fake store lists.</summary>
    public IReadOnlyList<ProviderConnectionInfo> Connections { get; set; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFinancialSyncService>();
            services.AddScoped<IFinancialSyncService>(_ => new FakeConnectorSyncService(this));
            services.RemoveAll<IProviderConnectionStore>();
            services.AddScoped<IProviderConnectionStore>(_ => new FakeConnectionStore(this));
        });
    }

    private sealed class FakeConnectorSyncService(ProviderFailureApiFactory owner) : IFinancialSyncService
    {
        public Task<string> ConnectAsync(CancellationToken ct = default) => Task.FromResult("fake-member");

        public Task<SyncResult> SyncAsync(CancellationToken ct = default) =>
            owner.Failure is { } ex ? Task.FromException<SyncResult>(ex) : Task.FromResult(SyncResultBuilder.A().Build());
    }

    private sealed class FakeConnectionStore(ProviderFailureApiFactory owner) : IProviderConnectionStore
    {
        public Task<IReadOnlyList<ProviderConnectionInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult(owner.Connections);

        public Task<string?> GetMemberProviderIdAsync(ConnectorId providerName, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task SaveAsync(ConnectorId providerName, string memberProviderId, string institution, string status, CancellationToken ct = default) => Task.CompletedTask;
    }
}
