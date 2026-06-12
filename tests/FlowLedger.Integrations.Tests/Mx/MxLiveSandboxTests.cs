using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Opt-in confirmation tests against the REAL MX sandbox. Kept skipped in CI and never require
/// credentials there. To run against a live MX Sandbox, set the environment variables and remove
/// the Skip:
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
    private const string SkipReason =
        "LiveMx: requires a real MX sandbox key (Mx__ApiKey). Skipped in CI by design — " +
        "remove Skip and set Mx__* env vars to run against the MX Sandbox.";

    private static IServiceProvider BuildProviderFromEnvironment()
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMxProvider(config);
        return services.BuildServiceProvider();
    }

    [Fact(Skip = SkipReason)]
    public async Task Live_round_trip_connect_accounts_transactions()
    {
        using var sp = (ServiceProvider)BuildProviderFromEnvironment();
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
