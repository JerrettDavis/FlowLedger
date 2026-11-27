using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Opt-in confirmation tests against the REAL MX sandbox. Skipped unless Mx__ApiKey is set (see LiveFact), so CI never
/// needs credentials. To run against a live MX Sandbox, set the environment variables:
///   set Mx__Enabled=true
///   set Mx__ApiKey=&lt;your-key&gt;
///   set Mx__ClientId=&lt;your-client-id&gt;
///   set Mx__BaseUrl=https://int-api.mx.com
///   set Mx__WebhookSecret=&lt;your-secret&gt;
///   dotnet test --filter "Category=LiveMx"
///
/// The tests build the provider from environment configuration (the same AddMxProvider path the
/// app uses) and exercise a real connect → status → accounts → transactions round trip.
/// </summary>
[Trait("Category", "LiveMx")]
public sealed class MxLiveSandboxTests
{
    private static ServiceProvider BuildProviderFromEnvironment()
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRawPayloadArchive>(new InMemoryRawPayloadArchive());
        services.AddMxProvider(config);
        return services.BuildServiceProvider();
    }

    [LiveFact("Mx__ApiKey")]
    public async Task Live_round_trip_connect_accounts_transactions()
    {
        using var sp = BuildProviderFromEnvironment();
        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IFinancialDataProvider>();

        var tenant = TenantId.From(Guid.NewGuid());
        var member = await provider.BeginConnectionAsync(tenant);
        member.ProviderId.Should().NotBeNullOrWhiteSpace();

        var status = await provider.GetConnectionStatusAsync(member.ProviderId);
        Enum.IsDefined(status).Should().BeTrue();

        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        accounts.Should().NotBeNull();

        if (accounts.Count > 0)
        {
            var page = await provider.GetTransactionsAsync(accounts[0].ProviderId, SyncCursor.Initial);
            page.Should().NotBeNull();
        }
    }
}
