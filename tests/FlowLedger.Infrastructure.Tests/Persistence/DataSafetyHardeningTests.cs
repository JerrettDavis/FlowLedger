using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class DataSafetyHardeningTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-hard-" + Guid.NewGuid().ToString("N"));

    public DataSafetyHardeningTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres", Pooling = false };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    [DockerFact]
    public async Task After_import_new_ledger_and_backup_rows_can_still_be_inserted()
    {
        var now = DateTimeOffset.UtcNow;
        var sourceLedger = new NpgsqlProviderCallLedger(_fixture.ConnectionString);
        for (var i = 0; i < 3; i++)
        {
            await sourceLedger.RecordAsync(new ProviderCallRecord("MX", "GET", "/users", 200, 5, 10, now));
        }

        await new BackupRunStore(_fixture.ConnectionString).AddAsync(
            new BackupRunResult(true, now, now, "x", 1, null, null, true, null));

        var zip = await SnapshotImport.ExportZipAsync(_fixture.ConnectionString, _root);
        var target = await CreateEmptyDatabaseAsync("flowledger_import_seq_test");
        await using (var stream = File.OpenRead(zip))
        {
            var result = await SnapshotImport.ImportZipAsync(target, stream, _root);
            result.Ok.Should().BeTrue(string.Join("; ", result.Problems));
        }

        // Without a sequence reset these inserts reuse id 1 and fail with a duplicate key.
        var targetLedger = new NpgsqlProviderCallLedger(target);
        await targetLedger.RecordAsync(new ProviderCallRecord("MX", "GET", "/after-import", 200, 5, 10, now));
        (await targetLedger.CountSinceAsync("MX", now.AddHours(-1))).Should().Be(4);

        await new BackupRunStore(target).AddAsync(new BackupRunResult(true, now, now, "y", 1, null, null, true, null));
        (await new BackupRunStore(target).RecentAsync(10)).Should().HaveCount(2);
    }

    [DockerFact]
    public async Task Advisory_lock_is_exclusive_across_connections_and_released_on_dispose()
    {
        await using var first = await PostgresAdvisoryLock.TryAcquireAsync(_fixture.ConnectionString, "flowledger-sync:test");
        first.Should().NotBeNull();

        var second = await PostgresAdvisoryLock.TryAcquireAsync(_fixture.ConnectionString, "flowledger-sync:test");
        second.Should().BeNull("another session already holds the lock");

        var other = await PostgresAdvisoryLock.TryAcquireAsync(_fixture.ConnectionString, "flowledger-sync:another-tenant");
        other.Should().NotBeNull("different names do not block each other");
        await other!.DisposeAsync();

        await first!.DisposeAsync();
        await using var third = await PostgresAdvisoryLock.TryAcquireAsync(_fixture.ConnectionString, "flowledger-sync:test");
        third.Should().NotBeNull("the lock is free again after dispose");
    }

    private sealed class CountingSync : IFinancialSyncService
    {
        public int Calls { get; private set; }

        public Task<string> ConnectAsync(CancellationToken ct = default) => Task.FromResult("m");

        public Task<SyncResult> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new SyncResult(0, 0, 0, 0));
        }
    }

    [DockerFact]
    public async Task Sync_in_another_process_holding_the_lock_makes_this_sync_refuse_without_calling_provider()
    {
        var tenant = Guid.NewGuid();
        var inner = new CountingSync();
        var conn = _fixture.ConnectionString;
        var guarded = new GuardedSyncService(inner, tenant, TimeSpan.Zero, _ => Task.FromResult<DateTimeOffset?>(null),
            crossProcessLock: async ct => await PostgresAdvisoryLock.TryAcquireAsync(conn, "flowledger-sync:" + tenant, ct));

        // Simulates the Worker already syncing this tenant.
        await using (var held = await PostgresAdvisoryLock.TryAcquireAsync(conn, "flowledger-sync:" + tenant))
        {
            held.Should().NotBeNull();
            var act = () => guarded.SyncAsync();
            await act.Should().ThrowAsync<SyncAlreadyRunningException>();
            inner.Calls.Should().Be(0);
        }

        await guarded.SyncAsync();
        inner.Calls.Should().Be(1);
    }
}
