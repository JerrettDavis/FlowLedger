using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// Integration tests for EfSyncCursorStore against a real Postgres database via Testcontainers.
/// Tests skip cleanly when Docker is not available.
/// </summary>
[Collection("Integration")]
public sealed class EfSyncCursorStoreTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public EfSyncCursorStoreTests(IntegrationTestFixture fixture)
        => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Get_returns_initial_when_no_record_exists()
    {
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var store = new EfSyncCursorStore(db, tenant);

        var result = await store.GetAsync("SimulatedProvider", "account-123");

        result.Should().BeEmpty("no cursor has been persisted yet");
    }

    [DockerFact]
    public async Task Set_then_Get_roundtrips_cursor_value()
    {
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var store = new EfSyncCursorStore(db, tenant);

        await store.SetAsync("SimulatedProvider", "account-123", "cursor-value-abc");

        var result = await store.GetAsync("SimulatedProvider", "account-123");
        result.Should().Be("cursor-value-abc");
    }

    [DockerFact]
    public async Task Set_is_idempotent_upsert_on_same_key()
    {
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var store = new EfSyncCursorStore(db, tenant);

        await store.SetAsync("SimulatedProvider", "account-123", "cursor-v1");
        await store.SetAsync("SimulatedProvider", "account-123", "cursor-v2");
        await store.SetAsync("SimulatedProvider", "account-123", "cursor-v3");

        var result = await store.GetAsync("SimulatedProvider", "account-123");
        result.Should().Be("cursor-v3", "last write wins on the same key");

        // Verify only one record was created (not three)
        await using var verifyDb = _fixture.CreateDbContext(tenant);
        var count = verifyDb.SyncCursors
            .Count(r => r.ProviderName == "SimulatedProvider" && r.ProviderAccountId == "account-123");
        count.Should().Be(1, "upsert should not create duplicate records");
    }

    [DockerFact]
    public async Task Cursors_are_isolated_per_tenant()
    {
        var tenant1 = TestTenantContext.New();
        var tenant2 = TestTenantContext.New();

        await using var db1 = _fixture.CreateDbContext(tenant1);
        var store1 = new EfSyncCursorStore(db1, tenant1);

        await using var db2 = _fixture.CreateDbContext(tenant2);
        var store2 = new EfSyncCursorStore(db2, tenant2);

        await store1.SetAsync("Provider", "account-A", "cursor-for-tenant1");
        await store2.SetAsync("Provider", "account-A", "cursor-for-tenant2");

        var result1 = await store1.GetAsync("Provider", "account-A");
        var result2 = await store2.GetAsync("Provider", "account-A");

        result1.Should().Be("cursor-for-tenant1");
        result2.Should().Be("cursor-for-tenant2");
    }

    [DockerFact]
    public async Task Cursors_are_isolated_per_provider_name()
    {
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var store = new EfSyncCursorStore(db, tenant);

        await store.SetAsync("ProviderA", "account-123", "cursor-for-A");
        await store.SetAsync("ProviderB", "account-123", "cursor-for-B");

        var resultA = await store.GetAsync("ProviderA", "account-123");
        var resultB = await store.GetAsync("ProviderB", "account-123");

        resultA.Should().Be("cursor-for-A");
        resultB.Should().Be("cursor-for-B");
    }
}
