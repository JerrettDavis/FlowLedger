using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#87 slice 2: suspicious_dismissals moved from an ad-hoc Npgsql table to EF (migration + tenant filter).</summary>
[Collection("Integration")]
public sealed class EfSuspiciousDismissalStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Dismissals_are_tenant_scoped_and_upsert_the_note()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var tx = Guid.NewGuid();

        await using (var db = fixture.CreateProductionStyleContext(a))
        {
            var store = new EfSuspiciousDismissalStore(db, a);
            await store.DismissAsync(tx, "first");
            await store.DismissAsync(tx, "second");
            (await store.ListIdsAsync()).Should().BeEquivalentTo([tx]);
        }

        await using (var db = fixture.CreateProductionStyleContext(b))
        {
            (await new EfSuspiciousDismissalStore(db, b).ListIdsAsync()).Should().BeEmpty("tenant B must not see tenant A's dismissals");
        }

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT note FROM suspicious_dismissals WHERE transaction_id = @x", conn);
        cmd.Parameters.AddWithValue("x", tx);
        ((string?)await cmd.ExecuteScalarAsync()).Should().Be("second");
    }

    [DockerFact]
    public async Task Migration_is_idempotent_over_a_table_the_old_store_already_created()
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var seed = new NpgsqlCommand(
            "INSERT INTO suspicious_dismissals (tenant_id, transaction_id, note) VALUES (gen_random_uuid(), gen_random_uuid(), 'keep')", conn))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using (var again = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS suspicious_dismissals (tenant_id uuid NOT NULL, transaction_id uuid NOT NULL, note text NULL, " +
            "dismissed_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_suspicious_dismissals\" PRIMARY KEY (tenant_id, transaction_id));", conn))
        {
            await again.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM suspicious_dismissals", conn);
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(1);
    }
}
