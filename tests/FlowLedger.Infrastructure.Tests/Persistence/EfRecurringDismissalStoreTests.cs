using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#87 slice 1: recurring dismissals moved from an ad-hoc Npgsql table to EF (migration + tenant filter).</summary>
[Collection("Integration")]
public sealed class EfRecurringDismissalStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Dismissals_are_tenant_scoped_and_idempotent()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();

        await using (var db = fixture.CreateProductionStyleContext(a))
        {
            var store = new EfRecurringDismissalStore(db, a);
            await store.AddAsync("netflix|15.99");
            await store.AddAsync("netflix|15.99");
            (await store.ListAsync()).Should().BeEquivalentTo(["netflix|15.99"]);
        }

        await using (var db = fixture.CreateProductionStyleContext(b))
        {
            (await new EfRecurringDismissalStore(db, b).ListAsync()).Should().BeEmpty("tenant B must not see tenant A's dismissals");
        }
    }

    [DockerFact]
    public async Task Migration_is_idempotent_over_a_table_the_old_store_already_created()
    {
        // Live databases already have this table (created at runtime by the removed Npgsql store). The migration's
        // SQL must not fail or lose rows there.
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var seed = new NpgsqlCommand(
            "INSERT INTO recurring_dismissals (tenant_id, suggestion_key) VALUES (gen_random_uuid(), 'keep-me')", conn))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using (var again = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS recurring_dismissals (tenant_id uuid NOT NULL, suggestion_key text NOT NULL, " +
            "dismissed_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_recurring_dismissals\" PRIMARY KEY (tenant_id, suggestion_key));", conn))
        {
            await again.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM recurring_dismissals", conn);
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(1);
    }
}
