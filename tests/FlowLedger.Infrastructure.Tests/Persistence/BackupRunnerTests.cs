using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class BackupRunnerTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-bk-" + Guid.NewGuid().ToString("N"));

    public BackupRunnerTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var tenantCtx = TestTenantContext.New();
        await using var ctx = _fixture.CreateDbContext(tenantCtx);
        ctx.Accounts.Add(Account.Create(TenantId.From(tenantCtx.TenantId), "Checking", AccountType.Checking, new Money(5m, Currency.Usd), FlowLedger.TestSupport.TestClock.Provider));
        ctx.Accounts.Add(Account.Create(TenantId.From(tenantCtx.TenantId), "Savings, \"main\"", AccountType.Savings, new Money(7m, Currency.Usd), FlowLedger.TestSupport.TestClock.Provider));
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private BackupRunner Runner(string? resticRepo = null, string restic = "restic")
    {
        var settings = new BackupSettings(_root, null, resticRepo, null, restic, 30, true);
        return new BackupRunner(_fixture.ConnectionString, settings, new BackupRunStore(_fixture.ConnectionString), NullLogger.Instance);
    }

    [DockerFact]
    public async Task Backup_creates_verified_snapshot_and_passes_restore_drill()
    {
        var result = await Runner().RunAsync();

        result.Ok.Should().BeTrue(result.Error);
        result.DrillOk.Should().BeTrue();
        result.OffsiteOk.Should().BeNull("no off-site repository is configured");
        result.OffsiteDetail.Should().Contain("No off-site");
        (await DatabaseSnapshotService.VerifyAsync(result.SnapshotDir!)).Ok.Should().BeTrue();

        var recent = await new BackupRunStore(_fixture.ConnectionString).RecentAsync(5);
        recent.Should().NotBeEmpty();
        recent[0].Ok.Should().BeTrue();
    }

    [DockerFact]
    public async Task The_drill_result_is_stored_with_the_run_including_live_counts_and_the_raw_archive_check()
    {
        var objects = Path.Combine(_root, "objects");
        var raw = Path.Combine(objects, "raw", "2026", "11");
        Directory.CreateDirectory(raw);
        var body = "{\"hello\":\"world\"}";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)))[..16];
        await File.WriteAllTextAsync(Path.Combine(raw, $"mx-accounts-{hash}.json"), body);
        var settings = new BackupSettings(_root, objects, null, null, "restic", 30, true);

        var result = await new BackupRunner(_fixture.ConnectionString, settings, new BackupRunStore(_fixture.ConnectionString), NullLogger.Instance).RunAsync();

        result.Ok.Should().BeTrue(result.Error);
        var stored = (await new BackupRunStore(_fixture.ConnectionString).RecentAsync(1)).Single().Drill;
        stored.Should().NotBeNull();
        stored!.RawArchiveChecked.Should().BeTrue();
        stored.RawObjectsVerified.Should().Be(1);
        stored.Problems.Should().BeEmpty();
        var accounts = stored.Tables.Single(t => t.Table == "accounts");
        accounts.ExpectedRows.Should().Be(2);
        accounts.RestoredRows.Should().Be(2);
        accounts.LiveRows.Should().Be(2, "the live database still holds both accounts");
        accounts.Ok.Should().BeTrue();
    }

    [DockerFact]
    public async Task A_run_without_a_raw_archive_records_that_it_was_not_checked()
    {
        await Runner().RunAsync();

        var drill = (await new BackupRunStore(_fixture.ConnectionString).RecentAsync(1)).Single().Drill;
        drill.Should().NotBeNull();
        drill!.RawArchiveChecked.Should().BeFalse();
        drill.RawObjectsVerified.Should().Be(0);
    }

    [DockerFact]
    public async Task Failing_offsite_marks_the_run_failed_and_is_recorded()
    {
        var result = await Runner(resticRepo: "/nonexistent/repo", restic: "definitely-not-restic-xyz").RunAsync();

        result.Ok.Should().BeFalse();
        result.OffsiteOk.Should().BeFalse();
        result.Error.Should().Contain("Off-site backup failed");

        var recent = await new BackupRunStore(_fixture.ConnectionString).RecentAsync(1);
        recent[0].Ok.Should().BeFalse();
    }

    [DockerFact]
    public async Task Drill_restores_every_table_with_matching_counts_and_cleans_up()
    {
        var (dir, manifest) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "drill-test");

        var drill = await SnapshotRestoreDrill.RunAsync(_fixture.ConnectionString, dir, "flowledger_drill_test");

        drill.Ok.Should().BeTrue(string.Join("; ", drill.Problems));
        drill.Tables.Single(t => t.Table == "accounts").RestoredRows.Should().Be(2);
        drill.Tables.Should().OnlyContain(t => t.Ok);
        drill.Tables.Should().OnlyContain(t => t.LiveRows != null, "the drill compares each restored table with the live database");
        drill.Tables.Single(t => t.Table == "accounts").LiveRows.Should().Be(2);
        manifest.Tables.Count.Should().BeGreaterThan(5);
    }

    [DockerFact]
    public async Task Drill_refuses_a_tampered_snapshot()
    {
        var (dir, _) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "drill-test");
        await File.AppendAllTextAsync(Path.Combine(dir, "accounts.csv"), "garbage\n");

        var drill = await SnapshotRestoreDrill.RunAsync(_fixture.ConnectionString, dir, "flowledger_drill_test");

        drill.Ok.Should().BeFalse();
    }
}
