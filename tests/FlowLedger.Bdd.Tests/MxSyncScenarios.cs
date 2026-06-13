using FlowLedger.Application.Abstractions;
using FlowLedger.Bdd.Tests.Support;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace FlowLedger.Bdd.Tests;

/// <summary>
/// Provider sync scenarios driven through the real <see cref="IFinancialSyncService"/> backed by the
/// real MX provider (wired via the public <c>AddMxProvider</c> DI extension — option (b), no
/// InternalsVisibleTo) pointed at an in-project WireMock server, real EF repositories, and
/// Testcontainers Postgres.
/// </summary>
[Feature("Financial sync — MX provider (WireMock)")]
[Collection(BddIntegrationCollection.Name)]
public partial class MxSyncScenarios : TinyBddXunitBase
{
    private readonly BddTestFixture _fixture;

    public MxSyncScenarios(BddTestFixture fixture, ITestOutputHelper output) : base(output)
        => _fixture = fixture;

    private static IReadOnlyDictionary<string, string?> MxConfig(string baseUrl) =>
        new Dictionary<string, string?>
        {
            ["Mx:Enabled"] = "true",
            ["Mx:BaseUrl"] = baseUrl,
            ["Mx:ClientId"] = "test-client",
            ["Mx:ApiKey"] = "test-key",
            ["Mx:WebhookSecret"] = "wiremock-test-secret",
            ["Mx:Environment"] = "sandbox",
            ["Mx:Provider:RecordsPerPage"] = MxWireMockServer.RecordsPerPage.ToString(),
        };

    // ── Scenario 5 ────────────────────────────────────────────────────────────

    // [DisableOptimization]: the TinyBDD source generator inlines local function bodies into a
    // generated method where they are out of scope. Disabling the optimizer routes this scenario
    // through the runtime ambient path, which resolves local functions correctly.
    [Scenario("Sync via the MX provider (WireMock) imports the dataset and is idempotent"), DockerFact, DisableOptimization]
    public async Task Sync_via_mx_provider_is_idempotent()
    {
        var tenant = TestTenantContext.New();
        using var mx = new MxWireMockServer();

        async Task<TestTenantContext> CleanDb()
        {
            await _fixture.ResetAsync();
            return tenant;
        }

        async Task<SyncResult> FirstSync(TestTenantContext t)
        {
            using var scope = _fixture.CreateScope(t, MxConfig(mx.BaseUrl));
            var sync = scope.Resolve<IFinancialSyncService>();
            return await sync.SyncAsync();
        }

        // TinyBDD passes the WHEN result to all Then/And steps.
        bool AssertImported(SyncResult first)
        {
            first.AccountsUpserted.Should().Be(2);
            first.TransactionsAdded.Should().Be(
                MxWireMockServer.CheckingTransactionCount + MxWireMockServer.SavingsTransactionCount);
            return true;
        }

        // Second idempotency check: receives the original WHEN SyncResult but opens a new scope
        // to run a second sync — the cursor persisted by the first sync prevents re-import.
        async Task<bool> SecondSyncIdempotent(SyncResult _)
        {
            using var scope = _fixture.CreateScope(tenant, MxConfig(mx.BaseUrl));
            var sync = scope.Resolve<IFinancialSyncService>();
            var second = await sync.SyncAsync();
            second.TransactionsAdded.Should().Be(0,
                "the persisted cursor means the second MX sync imports nothing new");
            return true;
        }

        await Given("a clean database and a fresh tenant", CleanDb)
            .When("the first MX sync runs", FirstSync)
            .Then("two accounts and 33 transactions are imported", AssertImported)
            .And("a second MX sync with a new service instance adds no new transactions", SecondSyncIdempotent)
            .AssertPassed();
    }
}
