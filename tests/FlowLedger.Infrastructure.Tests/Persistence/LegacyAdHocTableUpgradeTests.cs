using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// #154 / #87: live databases already hold the tables the old Npgsql stores created at runtime (with their original DDL and data).
/// Each "Move...ToEf" migration must adopt them without losing a row, and the EF-backed store must keep working on them.
/// </summary>
[Collection("Integration")]
public sealed class LegacyAdHocTableUpgradeTests : IAsyncLifetime
{
    private const string BeforeHostWideMoves = "20261127000000_AddSyncCursorLastSuccessfulSyncAt";

    private readonly IntegrationTestFixture _fixture;
    private readonly List<string> _databases = [];

    public LegacyAdHocTableUpgradeTests(IntegrationTestFixture fixture) => _fixture = fixture;

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

    private async Task<string> NewDatabaseAsync()
    {
        var name = "legacy_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        _databases.Add(name);
        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(connectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .Options;
        await using var ctx = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), UnscopedTenantContext.Instance);
        await ctx.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [DockerFact]
    public async Task Provider_call_ledger_and_backup_runs_adopt_the_legacy_tables_with_their_rows()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, BeforeHostWideMoves);
        await ExecAsync(cs, """
            CREATE TABLE IF NOT EXISTS provider_api_calls (
                id bigserial PRIMARY KEY, provider text NOT NULL, method text NOT NULL, path text NOT NULL, status_code int NULL,
                duration_ms bigint NOT NULL, response_bytes bigint NULL, occurred_utc timestamptz NOT NULL, error text NULL);
            CREATE INDEX IF NOT EXISTS ix_provider_api_calls_provider_time ON provider_api_calls (provider, occurred_utc);
            CREATE TABLE IF NOT EXISTS backup_runs (
                id bigserial PRIMARY KEY, started_utc timestamptz NOT NULL, finished_utc timestamptz NOT NULL, ok boolean NOT NULL,
                snapshot_dir text NULL, rows bigint NOT NULL, offsite_ok boolean NULL, offsite_detail text NULL, drill_ok boolean NULL, error text NULL);
            INSERT INTO provider_api_calls (provider, method, path, duration_ms, occurred_utc) VALUES ('MX', 'GET', '/old', 5, now());
            INSERT INTO backup_runs (started_utc, finished_utc, ok, rows) VALUES (now(), now(), true, 7);
            """);

        await MigrateToAsync(cs, null);

        (await CountAsync(cs, "provider_api_calls")).Should().Be(1);
        (await CountAsync(cs, "backup_runs")).Should().Be(1);
        var ledger = new ProviderCallLedger(cs);
        await ledger.RecordAsync(new ProviderCallRecord("MX", "GET", "/new", 200, 3, 10, FlowLedger.TestSupport.TestClock.Instant));
        (await ledger.CountSinceAsync("MX", FlowLedger.TestSupport.TestClock.Instant.AddDays(-1))).Should().Be(2);
        var store = new BackupRunStore(cs);
        await store.AddAsync(new BackupRunResult(true, FlowLedger.TestSupport.TestClock.Instant, FlowLedger.TestSupport.TestClock.Instant, null, 9, null, null, null, null));
        (await store.RecentAsync(10)).Should().HaveCount(2).And.Contain(r => r.Rows == 7);
    }

    [DockerFact]
    public async Task Audit_job_runs_and_webhook_replays_adopt_the_legacy_tables_with_their_rows()
    {
        var cs = await NewDatabaseAsync();
        await MigrateToAsync(cs, BeforeHostWideMoves);
        await ExecAsync(cs, """
            CREATE TABLE IF NOT EXISTS audit_events (
                id bigserial PRIMARY KEY, occurred_utc timestamptz NOT NULL, tenant_id uuid NULL, actor text NOT NULL, action text NOT NULL,
                target text NULL, remote_ip text NULL, trace_id text NULL, status_code int NULL);
            CREATE INDEX IF NOT EXISTS ix_audit_events_time ON audit_events (occurred_utc);
            CREATE INDEX IF NOT EXISTS ix_audit_events_tenant_time ON audit_events (tenant_id, occurred_utc);
            CREATE TABLE IF NOT EXISTS job_runs (
                id bigserial PRIMARY KEY, job text NOT NULL, started_utc timestamptz NOT NULL, finished_utc timestamptz NOT NULL,
                success boolean NOT NULL, error text NULL, attempt int NOT NULL DEFAULT 1);
            CREATE INDEX IF NOT EXISTS ix_job_runs_finished ON job_runs (finished_utc);
            CREATE TABLE IF NOT EXISTS webhook_replays (
                provider text NOT NULL, body_hash text NOT NULL, seen_utc timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (provider, body_hash));
            CREATE INDEX IF NOT EXISTS ix_webhook_replays_seen ON webhook_replays (seen_utc);
            INSERT INTO audit_events (occurred_utc, actor, action) VALUES (now(), 'old', 'old.action');
            INSERT INTO job_runs (job, started_utc, finished_utc, success) VALUES ('Old', now(), now(), true);
            INSERT INTO webhook_replays (provider, body_hash) VALUES ('MX', 'oldhash');
            """);

        await MigrateToAsync(cs, null);

        var host = new FlowLedger.Infrastructure.Persistence.HostDb(cs);
        await new EfAuditLog(host).AppendAsync(new AuditEvent("new.action", "me", Guid.NewGuid(), "t", "127.0.0.1", "trace", 200, FlowLedger.TestSupport.TestClock.Instant));
        (await CountAsync(cs, "audit_events")).Should().Be(2);
        var jobs = new EfJobRunStore(host);
        await jobs.AppendAsync(new JobRunEntry("New", FlowLedger.TestSupport.TestClock.Instant, FlowLedger.TestSupport.TestClock.Instant, true, null, 1));
        (await jobs.RecentAsync(10)).Should().HaveCount(2);
        var replays = new EfWebhookReplayStore(host);
        (await replays.TryRecordAsync("MX", "oldhash", TimeSpan.FromMinutes(10))).Should().BeFalse("the legacy row is still inside the window");
        (await replays.TryRecordAsync("MX", "newhash", TimeSpan.FromMinutes(10))).Should().BeTrue();
    }
}
