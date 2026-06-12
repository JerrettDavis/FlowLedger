using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Simulated;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// Integration tests for FinancialSyncService against the Simulated provider and a real
/// Postgres database (via Testcontainers). Tests skip cleanly when Docker is not available.
/// </summary>
[Collection("Integration")]
public sealed class FinancialSyncServiceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public FinancialSyncServiceTests(IntegrationTestFixture fixture)
        => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FinancialSyncService CreateSyncService(
        FlowLedgerDbContext db,
        TestTenantContext tenant)
    {
        var simOptions = Options.Create(new SimulatedProviderOptions());
        var provider = new SimulatedFinancialDataProvider(simOptions);

        return new FinancialSyncService(
            provider,
            db,
            tenant,
            NullLogger<FinancialSyncService>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task SyncAsync_FirstRun_PopulatesAccountsAndTransactions()
    {
        // Arrange
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var syncService = CreateSyncService(db, tenant);

        // Act
        var result = await syncService.SyncAsync();

        // Assert — accounts
        result.AccountsUpserted.Should().BeGreaterThan(0);
        result.TransactionsAdded.Should().BeGreaterThan(0);
        result.TransactionsSkipped.Should().Be(0);

        // Verify DB state — accounts persisted
        await using var verifyDb = _fixture.CreateDbContext(tenant);
        var accounts = await verifyDb.Accounts.ToListAsync();
        accounts.Should().NotBeEmpty("simulated provider returns at least 4 accounts");

        // Verify DB state — transactions persisted
        var transactions = await verifyDb.Transactions.ToListAsync();
        transactions.Should().NotBeEmpty("simulated provider returns historical transactions");
    }

    [DockerFact]
    public async Task SyncAsync_SecondRun_IsIdempotent_NoNewDuplicates()
    {
        // Arrange
        var tenant = TestTenantContext.New();
        await using var db1 = _fixture.CreateDbContext(tenant);
        var syncService1 = CreateSyncService(db1, tenant);

        // First sync
        var firstResult = await syncService1.SyncAsync();

        // Capture counts after first sync
        await using var countDb = _fixture.CreateDbContext(tenant);
        var accountCountAfterFirst = await countDb.Accounts.CountAsync();
        var txCountAfterFirst = await countDb.Transactions.CountAsync();

        // Act: second sync (new service instance = fresh cursor cache → full re-fetch)
        await using var db2 = _fixture.CreateDbContext(tenant);
        var syncService2 = CreateSyncService(db2, tenant);
        var secondResult = await syncService2.SyncAsync();

        // Assert — second sync skips all transactions (fingerprints already in DB)
        secondResult.TransactionsAdded.Should().Be(0,
            "all transactions from first sync should be fingerprint-deduplicated on second sync");
        secondResult.TransactionsSkipped.Should().Be(firstResult.TransactionsAdded,
            "all previously added transactions should be skipped on second run");

        // DB counts unchanged
        await using var finalDb = _fixture.CreateDbContext(tenant);
        var finalAccountCount = await finalDb.Accounts.CountAsync();
        var finalTxCount = await finalDb.Transactions.CountAsync();

        finalAccountCount.Should().Be(accountCountAfterFirst);
        finalTxCount.Should().Be(txCountAfterFirst);
    }

    [DockerFact]
    public async Task SyncAsync_TenantIsolation_DoesNotCrossContaminate()
    {
        // Arrange: two separate tenants sync independently
        var tenant1 = TestTenantContext.New();
        var tenant2 = TestTenantContext.New();

        await using var db1 = _fixture.CreateDbContext(tenant1);
        var sync1 = CreateSyncService(db1, tenant1);
        await sync1.SyncAsync();

        await using var db2 = _fixture.CreateDbContext(tenant2);
        var sync2 = CreateSyncService(db2, tenant2);
        await sync2.SyncAsync();

        // Each tenant's query filter should only return their own data
        await using var verify1 = _fixture.CreateDbContext(tenant1);
        var accts1 = await verify1.Accounts.ToListAsync();
        accts1.Should().OnlyContain(a => a.TenantId.Value == tenant1.TenantId);

        await using var verify2 = _fixture.CreateDbContext(tenant2);
        var accts2 = await verify2.Accounts.ToListAsync();
        accts2.Should().OnlyContain(a => a.TenantId.Value == tenant2.TenantId);
    }

    [DockerFact]
    public async Task ConnectAsync_ReturnsNonEmptyMemberId()
    {
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var syncService = CreateSyncService(db, tenant);

        var memberId = await syncService.ConnectAsync();
        memberId.Should().NotBeNullOrWhiteSpace();
    }
}
