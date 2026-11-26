using System.Net;
using System.Text;
using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.SimpleFin;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// Security invariant: the operator's single SimpleFIN access URL belongs to the household (legacy) tenant ONLY.
/// Any other tenant (e.g. a second OIDC user, or the scheduled worker fanning out over every tenant) must never
/// trigger a SimpleFIN call, never get a connection provisioned, and never receive the household's bank data.
/// No database is needed: every refusal happens before the DbContext is touched.
/// </summary>
[Trait("Invariant", "INV-01")]
public sealed class SimpleFinTenantIsolationTests
{
    private static readonly Guid Household = TenancySettings.DefaultLegacyTenantId;

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"errors":[],"accounts":[]}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ServiceProvider Sp, CountingHandler Handler) Build(Guid tenant, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:flowledger"] = "Host=localhost;Database=unused;Username=u;Password=p",
            ["SimpleFin:Enabled"] = "true",
            ["SimpleFin:AccessUrl"] = "https://user:secret@bridge.example/simplefin",
            ["Sync:MinIntervalMinutes"] = "0",
        };
        foreach (var kv in extra ?? [])
        {
            settings[kv.Key] = kv.Value;
        }

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddInfrastructure(cfg);
        FlowLedger.Application.DependencyInjection.AddApplication(services);
        services.AddHttpClient<SimpleFinFinancialDataProvider>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddScoped<ITenantContext>(_ => new TestTenantContext(tenant));
        return (services.BuildServiceProvider(), handler);
    }

    [Fact]
    public async Task Another_tenant_gets_the_unlinked_no_op_and_never_calls_SimpleFIN()
    {
        var (sp, handler) = Build(Guid.NewGuid());
        using var _ = sp;
        using var scope = sp.CreateScope();

        var sync = scope.ServiceProvider.GetRequiredService<IFinancialSyncService>();

        sync.Should().BeOfType<UnlinkedTenantSyncService>();
        var result = await sync.SyncAsync();
        result.AccountsUpserted.Should().Be(0);
        result.TransactionsAdded.Should().Be(0);
        handler.Calls.Should().Be(0, "the household's SimpleFIN credentials must never be used for another tenant");
    }

    [Fact]
    public async Task Another_tenant_connect_is_a_needs_user_action_refusal()
    {
        var (sp, handler) = Build(Guid.NewGuid());
        using var _ = sp;
        using var scope = sp.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IFinancialSyncService>().ConnectAsync();

        await act.Should().ThrowAsync<NeedsUserActionProviderException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Core_sync_service_refuses_a_non_household_tenant_even_when_resolved_directly()
    {
        // Defense in depth: even if a future caller bypasses the DI guard and resolves the concrete service.
        var (sp, handler) = Build(Guid.NewGuid());
        using var _ = sp;
        using var scope = sp.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<FinancialSyncService>();

        await core.Invoking(s => s.SyncAsync()).Should().ThrowAsync<NeedsUserActionProviderException>();
        await core.Invoking(s => s.ConnectAsync()).Should().ThrowAsync<NeedsUserActionProviderException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public void Household_tenant_gets_the_real_sync_chain()
    {
        var (sp, _) = Build(Household);
        using var __ = sp;
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IFinancialSyncService>().Should().NotBeOfType<UnlinkedTenantSyncService>();
    }

    [Theory]
    [InlineData("Tenancy:LegacyTenantId")]
    [InlineData("Api:TenantId")]
    [InlineData("Worker:TenantId")]
    public void Configured_household_tenant_is_honoured_and_the_default_one_is_then_refused(string key)
    {
        var configured = Guid.NewGuid();
        var (sp, _) = Build(configured, new() { [key] = configured.ToString() });
        using var __ = sp;
        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IFinancialSyncService>().Should().NotBeOfType<UnlinkedTenantSyncService>();
        }

        var (other, _) = Build(Household, new() { [key] = configured.ToString() });
        using var ___ = other;
        using var otherScope = other.CreateScope();
        otherScope.ServiceProvider.GetRequiredService<IFinancialSyncService>().Should().BeOfType<UnlinkedTenantSyncService>();
    }

    [Fact]
    public void Legacy_tenant_precedence_is_Tenancy_then_Api_then_Worker()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tenancy:LegacyTenantId"] = a.ToString(),
            ["Api:TenantId"] = b.ToString(),
            ["Worker:TenantId"] = c.ToString(),
        }).Build();
        TenancySettings.Legacy(cfg).LegacyTenantId.Should().Be(a);

        var noTenancy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:TenantId"] = b.ToString(),
            ["Worker:TenantId"] = c.ToString(),
        }).Build();
        TenancySettings.Legacy(noTenancy).LegacyTenantId.Should().Be(b);
    }
}
