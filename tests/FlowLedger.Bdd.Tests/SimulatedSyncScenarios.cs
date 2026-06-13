using FlowLedger.Application.Abstractions;
using FlowLedger.Bdd.Tests.Support;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace FlowLedger.Bdd.Tests;

/// <summary>
/// Provider sync scenarios driven through the real <see cref="IFinancialSyncService"/> backed by the
/// Simulated provider (Mx:Enabled = false), real EF repositories, and Testcontainers Postgres.
/// </summary>
[Feature("Financial sync — Simulated provider")]
[Collection(BddIntegrationCollection.Name)]
public partial class SimulatedSyncScenarios : TinyBddXunitBase
{
    private readonly BddTestFixture _fixture;

    public SimulatedSyncScenarios(BddTestFixture fixture, ITestOutputHelper output) : base(output)
        => _fixture = fixture;

    // ── Scenario 4 ────────────────────────────────────────────────────────────

    // [DisableOptimization]: the TinyBDD source generator inlines local function bodies into a
    // generated method where they are out of scope. Disabling the optimizer routes this scenario
    // through the runtime ambient path, which resolves local functions correctly.
    [Scenario("Sync via the Simulated provider populates accounts and is idempotent"), DockerFact, DisableOptimization]
    public async Task Sync_via_simulated_provider_is_idempotent()
    {
        var tenant = TestTenantContext.New();

        async Task<TestTenantContext> CleanDb()
        {
            await _fixture.ResetAsync();
            return tenant;
        }

        async Task<SyncResult> FirstSync(TestTenantContext t)
        {
            using var scope = _fixture.CreateScope(t);
            var sync = scope.Resolve<IFinancialSyncService>();
            return await sync.SyncAsync();
        }

        // TinyBDD passes the WHEN result to all Then/And steps.
        bool AssertImported(SyncResult first)
        {
            first.AccountsUpserted.Should().BeGreaterThan(0);
            first.TransactionsAdded.Should().BeGreaterThan(0);
            first.TransactionsSkipped.Should().Be(0);
            return true;
        }

        // Second idempotency check: receives the original WHEN SyncResult (first sync result)
        // but opens a fresh scope to run a second sync from scratch.
        async Task<bool> SecondSyncIdempotent(SyncResult _)
        {
            using var scope = _fixture.CreateScope(tenant);
            var sync = scope.Resolve<IFinancialSyncService>();
            var second = await sync.SyncAsync();
            second.TransactionsAdded.Should().Be(0,
                "the persisted cursor means the second sync imports nothing new");
            return true;
        }

        await Given("a clean database and a fresh tenant", CleanDb)
            .When("the first sync runs", FirstSync)
            .Then("accounts and transactions are imported", AssertImported)
            .And("a second sync with a new service instance adds no new transactions", SecondSyncIdempotent)
            .AssertPassed();
    }
}
