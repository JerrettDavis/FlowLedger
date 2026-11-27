using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#181: merge snapshots (financial PII) and spent link codes are pruned; the tombstone is kept forever.</summary>
[Collection("Integration")]
public sealed class DataRetentionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await ExecAsync("DELETE FROM merged_transactions; DELETE FROM app_user_link_codes;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [DockerFact]
    public async Task Old_merge_snapshots_are_blanked_but_the_tombstone_survives()
    {
        await ExecAsync(
            "INSERT INTO merged_transactions (tenant_id, duplicate_id, survivor_id, fingerprint, merged_utc, snapshot) VALUES " +
            "(gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'fp-old', now() - interval '200 days', '{\"description\":\"SECRET\"}')," +
            "(gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'fp-new', now() - interval '5 days', '{\"description\":\"RECENT\"}')");

        var result = await new DataRetention(fixture.ConnectionString).PruneAsync(snapshotAge: TimeSpan.FromDays(90), linkCodeAge: TimeSpan.FromHours(24));

        result.SnapshotsBlanked.Should().Be(1);
        (await ScalarAsync("SELECT count(*) FROM merged_transactions")).Should().Be(2, "tombstones are kept so re-imports cannot resurrect merged rows");
        (await ScalarAsync("SELECT count(*) FROM merged_transactions WHERE fingerprint = 'fp-old' AND snapshot = ''")).Should().Be(1);
        (await ScalarAsync("SELECT count(*) FROM merged_transactions WHERE fingerprint = 'fp-new' AND snapshot <> ''")).Should().Be(1);
    }

    [DockerFact]
    public async Task Expired_link_codes_older_than_the_cutoff_are_deleted_and_fresh_ones_kept()
    {
        await ExecAsync(
            "INSERT INTO app_user_link_codes (code_hash, tenant_id, created_utc, expires_utc) VALUES " +
            "('old', gen_random_uuid(), now() - interval '3 days', now() - interval '3 days' + interval '15 minutes')," +
            "('fresh', gen_random_uuid(), now(), now() + interval '15 minutes')");

        var result = await new DataRetention(fixture.ConnectionString).PruneAsync(TimeSpan.FromDays(90), TimeSpan.FromHours(24));

        result.LinkCodesDeleted.Should().Be(1);
        (await ScalarAsync("SELECT count(*) FROM app_user_link_codes WHERE code_hash = 'fresh'")).Should().Be(1);
    }

    [DockerFact]
    public async Task A_pruned_merge_still_blocks_re_creation_of_the_merged_row()
    {
        var tenant = TestTenantContext.New();
        await ExecAsync(
            "INSERT INTO merged_transactions (tenant_id, duplicate_id, survivor_id, fingerprint, merged_utc, snapshot) VALUES " +
            $"('{tenant.TenantId}', gen_random_uuid(), gen_random_uuid(), 'fp-merged', now() - interval '400 days', '{{\"description\":\"SECRET\"}}')");

        (await new DataRetention(fixture.ConnectionString).PruneAsync()).SnapshotsBlanked.Should().Be(1);

        await using var db = fixture.CreateDbContext(tenant);
        var tombstoned = await DuplicateStore.TombstonedAsync(db, ["fp-merged", "fp-other"], CancellationToken.None);
        tombstoned.Should().BeEquivalentTo(["fp-merged"], "sync and import consult the tombstone, which retention never deletes");
    }

    [DockerFact]
    public async Task Configured_ages_drive_the_prune()
    {
        await ExecAsync(
            "INSERT INTO merged_transactions (tenant_id, duplicate_id, survivor_id, fingerprint, merged_utc, snapshot) VALUES " +
            "(gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'fp-10d', now() - interval '10 days', '{\"d\":1}')");
        var settings = RetentionSettings.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retention:MergeSnapshotDays"] = "7" }).Build());

        var result = await new DataRetention(fixture.ConnectionString, settings).PruneAsync();

        result.SnapshotsBlanked.Should().Be(1, "10 days is older than the configured 7");
    }

    [Fact]
    public void Settings_default_when_missing_or_non_positive()
    {
        RetentionSettings.From(new ConfigurationBuilder().Build()).Should().Be(RetentionSettings.Default);
        RetentionSettings.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retention:MergeSnapshotDays"] = "0", ["Retention:LinkCodeGraceHours"] = "-3" })
            .Build()).Should().Be(RetentionSettings.Default);
        RetentionSettings.Default.MergeSnapshotAge.Should().Be(TimeSpan.FromDays(90));
        RetentionSettings.Default.LinkCodeAge.Should().Be(TimeSpan.FromHours(24));
    }
}
