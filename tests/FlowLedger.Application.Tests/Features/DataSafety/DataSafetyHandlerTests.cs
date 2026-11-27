using FlowLedger.Application.Features.DataSafety;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.TestSupport;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.DataSafety;

/// <summary>#94: the Data page composition (staleness, export scope, import hand-off) is Application logic, testable without HTTP.</summary>
public sealed class DataSafetyHandlerTests
{
    private static readonly DateTimeOffset Now = TestClock.Instant;

    private sealed class Reader(IReadOnlyList<BackupRunView> backups) : IDataSafetyReader
    {
        public string Environment => "sandbox";
        public bool OffsiteConfigured => true;
        public Task<IReadOnlyList<BackupRunView>> RecentBackupsAsync(int limit, CancellationToken ct = default) => Task.FromResult(backups);
        public Task<IReadOnlyList<JobRunView>> RecentJobRunsAsync(int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobRunView>>([]);
        public Task<ProviderUsageView> ProviderUsageAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.FromResult(new ProviderUsageView("MX", 3, 40, 500, 5000, false, []));
    }

    private static BackupRunView Run(bool ok, DateTimeOffset finished) => new(ok, finished.AddMinutes(-1), finished, "snap", 10, true, null, true, ok ? null : "boom");

    private static Task<DataStatus> Status(params BackupRunView[] runs) =>
        new GetDataStatusHandler(new Reader(runs), TestClock.Provider).HandleAsync();

    [Fact]
    public async Task A_recent_successful_run_is_healthy_and_reports_the_last_success()
    {
        var s = await Status(Run(true, Now.AddHours(-2)), Run(false, Now.AddDays(-2)));

        s.BackupHealthy.Should().BeTrue();
        s.BackupStale.Should().BeFalse();
        s.LastSuccessUtc.Should().Be(Now.AddHours(-2));
        s.Environment.Should().Be("sandbox");
        s.ProviderUsage.CallsThisMonth.Should().Be(40);
    }

    [Fact]
    public async Task No_runs_a_failed_latest_run_or_an_old_run_is_stale()
    {
        (await Status()).BackupStale.Should().BeTrue("nothing has ever run");
        (await Status(Run(false, Now.AddHours(-1)), Run(true, Now.AddHours(-5)))).BackupStale.Should().BeTrue("the latest run failed");
        (await Status(Run(true, Now - GetDataStatusHandler.StaleAfter - TimeSpan.FromMinutes(1)))).BackupStale.Should().BeTrue("older than 36 hours");
        (await Status(Run(true, Now - GetDataStatusHandler.StaleAfter + TimeSpan.FromMinutes(1)))).BackupStale.Should().BeFalse();
    }

    private sealed class Archive : IDataArchive
    {
        public Guid? ExportedFor { get; private set; }
        public bool Exported { get; private set; }

        public Task<string> ExportAsync(Guid? onlyTenant, CancellationToken ct = default)
        {
            Exported = true;
            ExportedFor = onlyTenant;
            return Task.FromResult("export.zip");
        }

        public Task<DataImportOutcome> ImportAsync(Stream zip, CancellationToken ct = default) =>
            Task.FromResult(new DataImportOutcome(false, ["database is not empty"], new { }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("all")]
    [InlineData("ALL")]
    public async Task Default_and_all_export_the_whole_database(string? scope)
    {
        var archive = new Archive();

        var outcome = await new ExportDataHandler(archive, new FakeTenantContext()).HandleAsync(scope, null);

        outcome.Refused.Should().BeFalse();
        outcome.AllTenants.Should().BeTrue();
        archive.ExportedFor.Should().BeNull();
    }

    [Fact]
    public async Task Tenant_scope_exports_the_callers_tenant_and_an_explicit_id_wins()
    {
        var tenant = new FakeTenantContext();
        var archive = new Archive();
        var handler = new ExportDataHandler(archive, tenant);

        (await handler.HandleAsync("tenant", null)).AllTenants.Should().BeFalse();
        archive.ExportedFor.Should().Be(tenant.TenantId);

        var other = Guid.NewGuid();
        await handler.HandleAsync(null, other);
        archive.ExportedFor.Should().Be(other);
    }

    [Fact]
    public async Task An_unknown_scope_is_refused_without_touching_the_archive()
    {
        var archive = new Archive();

        var outcome = await new ExportDataHandler(archive, new FakeTenantContext()).HandleAsync("everything", null);

        outcome.Refused.Should().BeTrue();
        outcome.Refusal.Should().Contain("scope");
        archive.Exported.Should().BeFalse();
    }

    [Fact]
    public async Task Import_hands_the_zip_to_the_archive_and_returns_its_outcome()
    {
        var outcome = await new ImportDataHandler(new Archive()).HandleAsync(new MemoryStream());

        outcome.Ok.Should().BeFalse();
        outcome.Problems.Should().ContainSingle().Which.Should().Contain("not empty");
    }
}
