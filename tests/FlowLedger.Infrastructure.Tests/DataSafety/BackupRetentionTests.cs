using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.Tests.DataSafety;

/// <summary>#189: the backup expiry policy (Backup:Retention:*) is bounded, documented and decided by pure functions.</summary>
public sealed class BackupRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static BackupRetention From(params (string Key, string Value)[] settings) =>
        BackupRetention.From(new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build());

    private static SnapshotFolder Backup(int daysOld) =>
        new($"/b/backup-{Now.AddDays(-daysOld):yyyyMMdd-HHmmss}", Now.AddDays(-daysOld));

    private static SnapshotFolder Premigrate(int daysOld) =>
        new($"/b/premigrate-{Now.AddDays(-daysOld):yyyyMMdd-HHmmss}", Now.AddDays(-daysOld));

    [Fact]
    public void Defaults_keep_30_local_7_daily_4_weekly_6_monthly_and_90_days_of_premigrate_snapshots()
    {
        var r = From();

        r.KeepLocalSnapshots.Should().Be(30);
        r.PremigrateMaxAgeDays.Should().Be(90);
        r.ResticForgetEnabled.Should().BeTrue();
        r.ResticForgetArguments().Should().Equal(
            "forget", "--tag", "flowledger", "--host", "flowledger", "--keep-daily", "7", "--keep-weekly", "4", "--keep-monthly", "6", "--prune");
    }

    [Fact]
    public void Configured_values_and_the_legacy_keep_setting_are_honoured_with_the_new_one_winning()
    {
        From(("Backup:KeepLocalSnapshots", "12")).KeepLocalSnapshots.Should().Be(12);
        From(("Backup:KeepLocalSnapshots", "12"), ("Backup:Retention:KeepLocalSnapshots", "5")).KeepLocalSnapshots.Should().Be(5);

        var r = From(("Backup:Retention:ResticKeepDaily", "3"), ("Backup:Retention:ResticKeepWeekly", "0"), ("Backup:Retention:ResticKeepMonthly", "24"));
        r.ResticForgetArguments().Should().Equal(
            "forget", "--tag", "flowledger", "--host", "flowledger", "--keep-daily", "3", "--keep-monthly", "24", "--prune");
    }

    [Fact]
    public void Off_site_expiry_can_be_switched_off_and_never_runs_without_a_keep_rule()
    {
        From(("Backup:Retention:ResticForget", "false")).ResticForgetArguments().Should().BeNull();
        From(("Backup:Retention:ResticKeepDaily", "0"), ("Backup:Retention:ResticKeepWeekly", "0"), ("Backup:Retention:ResticKeepMonthly", "0"))
            .ResticForgetArguments().Should().BeNull("restic refuses a forget with no keep rule");
    }

    [Fact]
    public void Unparseable_or_out_of_range_values_fall_back_to_the_defaults()
    {
        var r = From(("Backup:Retention:KeepLocalSnapshots", "0"), ("Backup:Retention:ResticKeepDaily", "-2"), ("Backup:Retention:PremigrateMaxAgeDays", "x"));

        r.KeepLocalSnapshots.Should().Be(30);
        r.ResticKeepDaily.Should().Be(7);
        r.PremigrateMaxAgeDays.Should().Be(90);
    }

    [Fact]
    public void Only_scheduled_snapshots_beyond_the_keep_count_and_older_than_a_week_are_deleted()
    {
        var policy = new BackupRetention(KeepLocalSnapshots: 3);
        var scheduled = new[] { Backup(1), Backup(2), Backup(3), Backup(4), Backup(10), Backup(30) };

        var doomed = policy.SelectLocalSnapshotsToDelete(scheduled, [], Now);

        // Keep the newest three (1, 2, 3 days old); of the rest, 4 days old is still inside the one-week floor.
        doomed.Should().BeEquivalentTo([Backup(10).Path, Backup(30).Path]);
    }

    [Fact]
    public void Premigrate_snapshots_expire_by_age_but_the_newest_is_always_kept()
    {
        var policy = new BackupRetention(PremigrateMaxAgeDays: 90);

        policy.SelectLocalSnapshotsToDelete([], [Premigrate(10), Premigrate(100), Premigrate(400)], Now)
            .Should().BeEquivalentTo([Premigrate(100).Path, Premigrate(400).Path]);
        policy.SelectLocalSnapshotsToDelete([], [Premigrate(400)], Now).Should().BeEmpty("the newest premigrate snapshot is the rollback point");
    }

    [Theory]
    [InlineData("backup-20261127-101500", 2026, 11, 27, 10, 15, 0)]
    [InlineData("premigrate-20250102-030405-2", 2025, 1, 2, 3, 4, 5)]
    public void A_snapshots_age_is_read_from_its_name_so_it_survives_a_copy_or_restore(string name, int y, int mo, int d, int h, int mi, int s)
    {
        var folder = SnapshotFolder.FromDirectory(Path.Combine(Path.GetTempPath(), "does-not-exist", name));

        folder.CreatedUtc.Should().Be(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero));
    }

    [Fact]
    public void Zero_premigrate_age_keeps_them_forever()
    {
        new BackupRetention(PremigrateMaxAgeDays: 0).SelectLocalSnapshotsToDelete([], [Premigrate(10), Premigrate(900)], Now).Should().BeEmpty();
    }
}
