using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Quiltt;
using FlowLedger.Integrations.SimpleFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Contract;

internal static class FixtureProviders
{
    public static SimpleFinFinancialDataProvider SimpleFin()
    {
        var handler = new FixtureHandler((_, _) => FixtureHandler.Json(FixtureFiles.Read("simplefin/accounts.json")));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bridge.example/simplefin/") };
        var opts = Options.Create(new SimpleFinOptions { Enabled = true, AccessUrl = "https://user:pass@bridge.example/simplefin" });
        return new SimpleFinFinancialDataProvider(http, opts, NullLogger<SimpleFinFinancialDataProvider>.Instance);
    }

    public static QuilttFinancialDataProvider Quiltt()
    {
        var handler = new FixtureHandler((req, body) =>
            req.RequestUri!.Host.StartsWith("auth", StringComparison.Ordinal) ? FixtureHandler.Json("""{"token":"tok","userId":"p_1"}""")
            : body.Contains("FlowLedgerAccounts", StringComparison.Ordinal) ? FixtureHandler.Json(FixtureFiles.Read("quiltt/accounts.json"))
            : FixtureHandler.Json(FixtureFiles.Read("quiltt/transactions.json")));
        var quilttOptions = new QuilttOptions { ApiKeySecret = "key", ConnectorId = "con_1", WebhookSecret = "whsec" };
        var options = Options.Create(quilttOptions);
        return new QuilttFinancialDataProvider(
            new QuilttApiClient(new HttpClient(handler), options), options,
            new QuilttWebhookVerifier("whsec", quilttOptions.WebhookTolerance), NullLogger<QuilttFinancialDataProvider>.Instance);
    }
}

/// <summary>Runs the full provider contract against SimpleFIN, fed by the sanitised <c>simplefin/accounts.json</c> fixture.</summary>
[Trait("Provider", "SimpleFIN")]
public sealed class SimpleFinProviderContractTests : FinancialProviderContractTests
{
    protected override IFinancialDataProvider CreateProvider() => FixtureProviders.SimpleFin();
}

/// <summary>Runs the full provider contract against Quiltt, fed by the sanitised <c>quiltt/*.json</c> fixtures.</summary>
[Trait("Provider", "Quiltt")]
public sealed class QuilttProviderContractTests : FinancialProviderContractTests
{
    protected override IFinancialDataProvider CreateProvider() => FixtureProviders.Quiltt();
}
