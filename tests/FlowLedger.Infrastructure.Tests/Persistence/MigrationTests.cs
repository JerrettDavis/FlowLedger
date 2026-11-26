using System.Reflection;
using System.Text.RegularExpressions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// Issue #140: model-drift and upgrade-path guards. Alphas upgrade in place on real data, so a migration that drifts from the
/// model, cannot be reversed, or loses rows must fail CI. Docker-backed tests each use their own throwaway database inside the
/// shared Postgres container, so they never disturb the shared migrated database.
/// </summary>
[Collection("Integration")]
public sealed class MigrationTests : IAsyncLifetime
{
    private const string Initial = "20260612041641_InitialCreate";
    private const string BillSheet = "20261114043850_AddBillSheet";
    private const string DedupeBills = "20261114050656_DedupeDetectedBills";
    private const string BeforeRecurringBillFrequency = "20261114064418_AddTransactionCategorySource";
    private const string RecurringBillFrequency = "20261115201318_AddRecurringBillFrequency";
    private const string BeforeAccountMode = "20261118040714_AddAccountTypeLock";
    private const string AccountMode = "20261119030000_AddAccountModeAndHiddenTransactions";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IntegrationTestFixture _fixture;
    private readonly List<string> _databases = [];

    public MigrationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(_fixture.ConnectionString);
        await admin.OpenAsync();
        foreach (var db in _databases)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // ── model drift (no database needed) ─────────────────────────────────────

    private static FlowLedgerDbContext OfflineContext() =>
        new(new DbContextOptionsBuilder<FlowLedgerDbContext>()
                .UseNpgsql("Host=localhost;Database=never_connected;Username=x;Password=x",
                    n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
                .Options,
            new NoOpDomainEventDispatcher(),
            tenantContext: FlowLedger.Infrastructure.Persistence.UnscopedTenantContext.Instance);

    [Fact]
    public void Model_snapshot_matches_the_current_model_so_no_migration_is_missing()
    {
        using var ctx = OfflineContext();

        ctx.Database.HasPendingModelChanges().Should().BeFalse(
            "the EF model changed without a migration: run `dotnet ef migrations add <Name>` and commit the migration");
    }

    [Fact]
    public void Every_migration_class_is_discovered_in_order_with_a_unique_id()
    {
        using var ctx = OfflineContext();
        var discovered = ctx.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        var declared = typeof(FlowLedgerDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        discovered.Should().HaveCount(declared.Count,
            "a Migration class without [Migration]/[DbContext] attributes is silently ignored and never runs");
        discovered.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        discovered.First().Should().Be(Initial);
    }

    [Fact]
    public void Every_migration_is_reversible_by_implementing_Down_or_is_explicitly_irreversible()
    {
        // Down must at least be implemented (not the base no-op that silently leaves the schema changed). A migration that
        // intentionally cannot be reversed must throw NotSupportedException/InvalidOperationException from Down and be listed here.
        var irreversible = new HashSet<string>(StringComparer.Ordinal);
        using var ctx = OfflineContext();
        var offenders = new List<string>();
        foreach (var (id, type) in ctx.GetService<IMigrationsAssembly>().Migrations)
        {
            var migration = ctx.GetService<IMigrationsAssembly>().CreateMigration(type, "Npgsql.EntityFrameworkCore.PostgreSQL");
            var down = migration.DownOperations;
            if (down.Count == 0 && !irreversible.Contains(id))
            {
                offenders.Add(id);
            }
        }

        offenders.Should().BeEmpty("these migrations have an empty Down; implement it or list them as irreversible with a reason");
    }

    // ── EF tables and ad-hoc tables must be disjoint ─────────────────────────

    [Fact]
    public void Ad_hoc_tables_and_EF_tables_do_not_overlap()
    {
        using var ctx = OfflineContext();
        var efTables = ctx.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var adHoc = Regex.Matches(AdHocSchema.AllSql, @"CREATE TABLE IF NOT EXISTS\s+(\w+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToList();

        adHoc.Should().NotBeEmpty();
        adHoc.Should().NotIntersectWith(efTables!, "a table created by both EF and a store's CREATE TABLE IF NOT EXISTS has two owners and will drift");
    }

    // ── upgrade paths on a real Postgres ─────────────────────────────────────

    private async Task<string> NewDatabaseAsync()
    {
        var name = "mig_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        _databases.Add(name);
        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static FlowLedgerDbContext ContextFor(string connectionString) =>
        new(new DbContextOptionsBuilder<FlowLedgerDbContext>()
                .UseNpgsql(connectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
                .Options,
            new NoOpDomainEventDispatcher(),
            tenantContext: FlowLedger.Infrastructure.Persistence.UnscopedTenantContext.Instance);

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        await using var ctx = ContextFor(connectionString);
        await ctx.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<int> ExecAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    /// <summary>Inserts one row giving every NOT NULL column without a default a type-appropriate value; <paramref name="values"/> override.</summary>
    private static async Task InsertRowAsync(string connectionString, string table, Dictionary<string, object> values)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var columns = new Dictionary<string, object>(values);
        await using (var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema='public' AND table_name=@t AND is_nullable='NO' AND column_default IS NULL AND is_identity='NO'", conn))
        {
            cmd.Parameters.AddWithValue("t", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (columns.ContainsKey(name))
                {
                    continue;
                }

                columns[name] = reader.GetString(1) switch
                {
                    "uuid" => Guid.NewGuid(),
                    "boolean" => false,
                    "integer" or "smallint" or "bigint" => 0,
                    "numeric" => 0m,
                    "date" => new DateOnly(2026, 1, 1),
                    "timestamp with time zone" => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    _ => "x",
                };
            }
        }

        var names = columns.Keys.ToList();
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO {table} ({string.Join(',', names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(',', names.Select((_, i) => $"@p{i}"))})", conn);
        for (var i = 0; i < names.Count; i++)
        {
            insert.Parameters.AddWithValue($"p{i}", columns[names[i]]);
        }

        await insert.ExecuteNonQueryAsync();
    }

    [DockerFact]
    public async Task Full_chain_from_empty_applies_and_rerunning_is_a_no_op()
    {
        var cs = await NewDatabaseAsync();

        await MigrateToAsync(cs, null);
        await using (var ctx = ContextFor(cs))
        {
            (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            var applied = (await ctx.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Equal(ctx.GetService<IMigrationsAssembly>().Migrations.Keys);
        }

        var before = await ScalarAsync<long>(cs, "SELECT count(*) FROM \"__EFMigrationsHistory\"");
        await MigrateToAsync(cs, null);
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM \"__EFMigrationsHistory\"")).Should().Be(before);
    }

    [DockerFact]
    public async Task Every_migration_can_be_rolled_back_to_empty_and_reapplied()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, null);

        // Walk down one migration at a time so a broken Down names the migration that caused it.
        List<string> ids;
        await using (var ctx = ContextFor(cs))
        {
            ids = ctx.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        }

        for (var i = ids.Count - 1; i >= 1; i--)
        {
            var act = () => MigrateToAsync(cs, ids[i - 1]);
            await act.Should().NotThrowAsync($"Down of {ids[i]} must work");
        }

        await MigrateToAsync(cs, null);
        await using var final = ContextFor(cs);
        (await final.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task DedupeDetectedBills_keeps_the_oldest_detected_row_per_name_and_never_touches_other_rows()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, BillSheet);

        var oldest = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var otherName = Guid.NewGuid();
        var manual = Guid.NewGuid();
        var manualSameName = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        async Task Bill(Guid id, string section, string name, int minutes) =>
            await InsertRowAsync(cs, "sheet_bills", new() { ["id"] = id, ["tenant_id"] = Tenant, ["section"] = section, ["name"] = name, ["created_at"] = t0.AddMinutes(minutes) });

        await Bill(duplicate, "Detected", "Power Co", 5);
        await Bill(oldest, "Detected", "Power Co", 1);
        await Bill(otherName, "Detected", "Water", 2);
        await Bill(manual, "Utilities", "Power Co", 3);
        await Bill(manualSameName, "Utilities", "Power Co", 4);
        await InsertRowAsync(cs, "sheet_payments", new() { ["tenant_id"] = Tenant, ["bill_id"] = oldest, ["month"] = new DateOnly(2026, 1, 1) });

        await MigrateToAsync(cs, DedupeBills);

        var remaining = await ReadIdsAsync(cs, "SELECT id FROM sheet_bills");
        remaining.Should().BeEquivalentTo([oldest, otherName, manual, manualSameName], "only the newer duplicate Detected row is removed");
        (await ScalarAsync<long>(cs, "SELECT count(*) FROM sheet_payments")).Should().Be(1, "the surviving row's payment is preserved");
    }

    [DockerFact]
    public async Task AddRecurringBillFrequency_preserves_bills_and_payments_and_defaults_to_monthly_slot_zero()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, BeforeRecurringBillFrequency);
        var bill = Guid.NewGuid();
        await InsertRowAsync(cs, "sheet_bills", new() { ["id"] = bill, ["tenant_id"] = Tenant, ["name"] = "Mowing", ["estimated"] = 55m });
        await InsertRowAsync(cs, "sheet_payments", new() { ["tenant_id"] = Tenant, ["bill_id"] = bill, ["month"] = new DateOnly(2026, 1, 1), ["is_paid"] = true });

        await MigrateToAsync(cs, RecurringBillFrequency);

        (await ScalarAsync<string>(cs, $"SELECT frequency FROM sheet_bills WHERE id = '{bill}'")).Should().Be("Monthly");
        (await ScalarAsync<int>(cs, "SELECT slot FROM sheet_payments")).Should().Be(0);
        (await ScalarAsync<decimal>(cs, $"SELECT estimated FROM sheet_bills WHERE id = '{bill}'")).Should().Be(55m);
        (await ScalarAsync<bool>(cs, "SELECT is_paid FROM sheet_payments")).Should().BeTrue();
    }

    [DockerFact]
    public async Task AddAccountMode_marks_existing_investment_accounts_as_holding_and_leaves_others_standard()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, BeforeAccountMode);
        var investment = Guid.NewGuid();
        var checking = Guid.NewGuid();
        await InsertRowAsync(cs, "accounts", new() { ["id"] = investment, ["tenant_id"] = Tenant, ["name"] = "Brokerage", ["account_type"] = "Investment" });
        await InsertRowAsync(cs, "accounts", new() { ["id"] = checking, ["tenant_id"] = Tenant, ["name"] = "Checking", ["account_type"] = "Checking" });

        await MigrateToAsync(cs, AccountMode);

        (await ScalarAsync<string>(cs, $"SELECT mode FROM accounts WHERE id = '{investment}'")).Should().Be("Holding");
        (await ScalarAsync<string>(cs, $"SELECT mode FROM accounts WHERE id = '{checking}'")).Should().Be("Standard");
        (await ScalarAsync<bool>(cs, $"SELECT hide_transactions FROM accounts WHERE id = '{investment}'")).Should().BeFalse();
    }

    [DockerFact]
    public async Task Populated_database_from_an_early_version_upgrades_to_head_without_losing_rows()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, BillSheet);

        var account = Guid.NewGuid();
        await InsertRowAsync(cs, "accounts", new() { ["id"] = account, ["tenant_id"] = Tenant, ["name"] = "Old checking", ["account_type"] = "Checking" });
        for (var i = 0; i < 5; i++)
        {
            await InsertRowAsync(cs, "transactions", new() { ["tenant_id"] = Tenant, ["account_id"] = account, ["amount"] = 10m + i, ["description"] = "tx " + i });
        }

        await InsertRowAsync(cs, "sheet_bills", new() { ["tenant_id"] = Tenant, ["name"] = "Rent", ["section"] = "Housing" });
        string[] tables = ["accounts", "transactions", "sheet_bills"];
        var before = new Dictionary<string, long>();
        foreach (var t in tables)
        {
            before[t] = await ScalarAsync<long>(cs, $"SELECT count(*) FROM {t}");
        }

        await MigrateToAsync(cs, null);

        foreach (var t in tables)
        {
            (await ScalarAsync<long>(cs, $"SELECT count(*) FROM {t}")).Should().Be(before[t], $"table {t} lost rows during the upgrade to head");
        }

        (await ScalarAsync<decimal>(cs, "SELECT sum(amount) FROM transactions")).Should().Be(10m + 11 + 12 + 13 + 14);
    }

    private static async Task<List<Guid>> ReadIdsAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
