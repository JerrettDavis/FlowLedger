using System.Net;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.Integrations.Quiltt;
using FlowLedger.Integrations.SimpleFin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Integrations.Tests;

/// <summary>#100: every real connector's HttpClient passes through the shared call-ledger and budget guard.</summary>
public sealed class ConnectorCallGuardWiringTests
{
    private sealed class FakeLedger : IProviderCallLedger
    {
        public List<ProviderCallRecord> Calls { get; } = [];

        public long Count { get; set; }

        public Task RecordAsync(ProviderCallRecord call, CancellationToken ct = default)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }

        public Task<long> CountSinceAsync(string provider, DateTimeOffset sinceUtc, CancellationToken ct = default) => Task.FromResult(Count);
    }

    private sealed class StubPrimary : HttpMessageHandler
    {
        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    public static TheoryData<string> Connectors => new() { ConnectorIds.Mx, ConnectorIds.Quiltt, ConnectorIds.SimpleFin };

    private static (HttpClient Client, StubPrimary Primary, FakeLedger Ledger) Build(string connector, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mx:BaseUrl"] = "https://mx.example",
            ["Mx:ClientId"] = "id",
            ["Mx:ApiKey"] = "key",
            ["Mx:WebhookSecret"] = "whsec",
            ["Quiltt:ApiKeySecret"] = "key",
        };
        foreach (var kv in extra ?? [])
        {
            settings[kv.Key] = kv.Value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var ledger = new FakeLedger();
        var primary = new StubPrimary();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderCallLedger>(ledger);
        string clientName;
        switch (connector)
        {
            case ConnectorIds.Mx:
                services.AddMxProvider(configuration);
                clientName = "MxApiClient";
                break;
            case ConnectorIds.Quiltt:
                services.AddQuilttProvider(configuration);
                clientName = "QuilttApiClient";
                break;
            default:
                services.AddSimpleFinProvider(configuration);
                clientName = "SimpleFinFinancialDataProvider";
                break;
        }

        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => primary);
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        return (client, primary, ledger);
    }

    [Theory]
    [MemberData(nameof(Connectors))]
    public async Task Every_connector_client_records_its_calls_in_the_ledger(string connector)
    {
        var (client, primary, ledger) = Build(connector);

        (await client.GetAsync(new Uri("https://provider.example/accounts?secret=1"))).StatusCode.Should().Be(HttpStatusCode.OK);

        primary.Sent.Should().Be(1);
        var call = ledger.Calls.Should().ContainSingle().Subject;
        call.Provider.Should().Be(connector);
        call.Path.Should().Be("/accounts");
    }

    [Theory]
    [MemberData(nameof(Connectors))]
    public async Task Every_connector_client_stops_at_its_daily_budget(string connector)
    {
        var (client, primary, ledger) = Build(connector);
        ledger.Count = 100_000;

        var act = () => client.GetAsync(new Uri("https://provider.example/accounts"));

        await act.Should().ThrowAsync<ProviderCallBudgetExceededException>().WithMessage("*Daily*");
        primary.Sent.Should().Be(0);
    }

    [Theory]
    [InlineData(ConnectorIds.Quiltt, "Quiltt:CallsPaused")]
    [InlineData(ConnectorIds.SimpleFin, "SimpleFin:CallsPaused")]
    public async Task The_kill_switch_stops_Quiltt_and_SimpleFin(string connector, string key)
    {
        var (client, primary, _) = Build(connector, new() { [key] = "true" });

        var act = () => client.GetAsync(new Uri("https://provider.example/accounts"));

        await act.Should().ThrowAsync<ProviderCallBudgetExceededException>().WithMessage("*paused*");
        primary.Sent.Should().Be(0);
    }
}
