using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#189: the scheduled backup job enforces Backup:Retention (local expiry and restic forget --prune); on-demand runs never delete.</summary>
[Collection("Integration")]
public sealed class BackupRetentionRunTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-ret-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        var tenant = TestTenantContext.New();
        await using var ctx = fixture.CreateDbContext(tenant);
        ctx.Accounts.Add(Account.Create(TenantId.From(tenant.TenantId), "Checking", AccountType.Checking, new Money(5m, Currency.Usd), FlowLedger.TestSupport.TestClock.Provider));
        await ctx.SaveChangesAsync();
        Directory.CreateDirectory(_root);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>A stand-in for restic that exits 0 and appends its arguments to restic.log next to it.</summary>
    private string FakeRestic()
    {
        var log = Path.Combine(_root, "restic.log");
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_root, "fake-restic.cmd");
            File.WriteAllText(cmd, $"@echo off\r\necho %* >> \"{log}\"\r\nexit /b 0\r\n");
            return cmd;
        }

        var sh = Path.Combine(_root, "fake-restic.sh");
        File.WriteAllText(sh, $"#!/bin/sh\necho \"$@\" >> \"{log}\"\nexit 0\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private string[] ResticCalls() =>
        File.Exists(Path.Combine(_root, "restic.log")) ? File.ReadAllLines(Path.Combine(_root, "restic.log")) : [];

    private BackupRunner Runner(BackupRetention retention, string? restic = null)
    {
        var settings = new BackupSettings(
            Path.Combine(_root, "backups"), null, restic is null ? null : Path.Combine(_root, "repo"), null, restic ?? "restic", retention.KeepLocalSnapshots, DrillAfterBackup: false, retention);
        return new BackupRunner(fixture.ConnectionString, settings, new BackupRunStore(fixture.ConnectionString), NullLogger.Instance);
    }

    private string MakeOld(string name)
    {
        var dir = Path.Combine(_root, "backups", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [DockerFact]
    public async Task The_scheduled_run_applies_restic_forget_prune_with_the_configured_keep_rules_after_the_copy()
    {
        var retention = new BackupRetention(ResticKeepDaily: 2, ResticKeepWeekly: 0, ResticKeepMonthly: 3);

        var result = await Runner(retention, FakeRestic()).RunAsync(allowPrune: true);

        result.Ok.Should().BeTrue(result.Error);
        var calls = ResticCalls();
        calls.Should().Contain(c => c.Contains("backup --tag flowledger", StringComparison.Ordinal));
        var forget = calls.Single(c => c.Contains("forget", StringComparison.Ordinal));
        forget.Should().Contain("--keep-daily 2").And.Contain("--keep-monthly 3").And.Contain("--prune").And.NotContain("--keep-weekly");
        forget.Should().Contain("--tag flowledger").And.Contain("--host flowledger");
        Array.FindIndex(calls, c => c.Contains("forget", StringComparison.Ordinal)).Should().BeGreaterThan(
            Array.FindIndex(calls, c => c.Contains("backup --tag", StringComparison.Ordinal)), "expiry runs only after the new copy exists");
        result.OffsiteDetail.Should().Contain("retention applied");
    }

    [DockerFact]
    public async Task An_on_demand_run_never_expires_anything()
    {
        var result = await Runner(BackupRetention.Default, FakeRestic()).RunAsync(allowPrune: false);

        result.Ok.Should().BeTrue(result.Error);
        ResticCalls().Should().NotContain(c => c.Contains("forget", StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task Disabled_off_site_expiry_runs_no_forget()
    {
        var result = await Runner(BackupRetention.Default with { ResticForget = false }, FakeRestic()).RunAsync(allowPrune: true);

        result.Ok.Should().BeTrue(result.Error);
        ResticCalls().Should().NotContain(c => c.Contains("forget", StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task The_scheduled_run_deletes_expired_local_and_premigrate_snapshots_and_keeps_the_newest_premigrate()
    {
        var oldBackup = MakeOld("backup-20250101-000000");
        var olderBackup = MakeOld("backup-20240101-000000");
        var oldPremigrate = MakeOld("premigrate-20250101-000000");
        var newestPremigrate = MakeOld("premigrate-20250601-000000");
        var retention = new BackupRetention(KeepLocalSnapshots: 1, PremigrateMaxAgeDays: 30);

        var result = await Runner(retention).RunAsync(allowPrune: true);

        result.Ok.Should().BeTrue(result.Error);
        Directory.Exists(result.SnapshotDir).Should().BeTrue("the snapshot this run just took is the newest");
        Directory.Exists(oldBackup).Should().BeFalse();
        Directory.Exists(olderBackup).Should().BeFalse();
        Directory.Exists(oldPremigrate).Should().BeFalse();
        Directory.Exists(newestPremigrate).Should().BeTrue("the newest premigrate snapshot is always kept as the rollback point");
    }

    [DockerFact]
    public async Task An_on_demand_run_keeps_old_snapshots()
    {
        var oldBackup = MakeOld("backup-20240101-000000");
        var oldPremigrate = MakeOld("premigrate-20240101-000000");

        var result = await Runner(new BackupRetention(KeepLocalSnapshots: 1, PremigrateMaxAgeDays: 30)).RunAsync(allowPrune: false);

        result.Ok.Should().BeTrue(result.Error);
        Directory.Exists(oldBackup).Should().BeTrue();
        Directory.Exists(oldPremigrate).Should().BeTrue();
    }
}
