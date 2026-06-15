using FlowLedger.Application.Abstractions;
using FlowLedger.Worker.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;

namespace FlowLedger.Worker.Tests.Jobs;

/// <summary>
/// Unit tests for <see cref="ScheduledSyncJob"/>.
/// Uses in-process fakes — no infrastructure, no Docker.
/// </summary>
public sealed class ScheduledSyncJobTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScheduledSyncJob BuildJob(
        IFinancialSyncService syncService,
        bool enabled = true,
        string cron = "0 0 */4 * * ?")
    {
        var services = new ServiceCollection();
        services.AddScoped<IFinancialSyncService>(_ => syncService);

        var options = Options.Create(new JobScheduleOptions
        {
            Enabled = enabled,
            CronExpression = cron
        });

        return new ScheduledSyncJob(
            services.BuildServiceProvider(),
            NullLogger<ScheduledSyncJob>.Instance,
            options);
    }

    private static IJobExecutionContext FakeContext() =>
        new FakeJobExecutionContext();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduledSyncJob_invokes_sync_service_once_per_fire()
    {
        // Arrange
        var syncSpy = new SyncServiceSpy(syncResult: new SyncResult(1, 5, 2, 7));
        var job = BuildJob(syncSpy);

        // Act
        await job.Execute(FakeContext());

        // Assert
        syncSpy.SyncCallCount.Should().Be(1, "the job should call SyncAsync exactly once per fire");
    }

    [Fact]
    public async Task ScheduledSyncJob_respects_disabled_flag()
    {
        // Arrange — job disabled via options
        var syncSpy = new SyncServiceSpy(syncResult: new SyncResult(0, 0, 0, 0));
        var job = BuildJob(syncSpy, enabled: false);

        // Act
        await job.Execute(FakeContext());

        // Assert — sync must NOT have been called
        syncSpy.SyncCallCount.Should().Be(0,
            "a disabled job must not invoke the sync service");
    }

    [Fact]
    public async Task ScheduledSyncJob_logs_and_does_not_throw_on_transient_provider_error()
    {
        // Arrange — sync service throws a transient error
        var faultingService = new FaultingSyncService("Simulated transient provider error");
        var job = BuildJob(faultingService);

        // Act & Assert — the job must swallow the exception and not crash the worker
        var act = () => job.Execute(FakeContext());
        await act.Should().NotThrowAsync(
            "transient provider errors must be caught and logged, not rethrown");
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class SyncServiceSpy(SyncResult syncResult) : IFinancialSyncService
    {
        public int SyncCallCount { get; private set; }

        public Task<string> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult("member-id-test");

        public Task<SyncResult> SyncAsync(CancellationToken ct = default)
        {
            SyncCallCount++;
            return Task.FromResult(syncResult);
        }
    }

    private sealed class FaultingSyncService(string message) : IFinancialSyncService
    {
        public Task<string> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult("member-id");

        public Task<SyncResult> SyncAsync(CancellationToken ct = default)
            => throw new HttpRequestException(message);
    }

    /// <summary>Minimal Quartz IJobExecutionContext fake — only CancellationToken is used by the job.</summary>
    private sealed class FakeJobExecutionContext : IJobExecutionContext
    {
        public CancellationToken CancellationToken => CancellationToken.None;

        // ── Unused members — throw if called unexpectedly ──────────────────
        public IScheduler Scheduler => throw new NotImplementedException();
        public ITrigger Trigger => throw new NotImplementedException();
        public ICalendar? Calendar => throw new NotImplementedException();
        public bool Recovering => false;
        public TriggerKey RecoveringTriggerKey => throw new NotImplementedException();
        public int RefireCount => 0;
        public JobDataMap MergedJobDataMap => new();
        public IJobDetail JobDetail => throw new NotImplementedException();
        public IJob JobInstance => throw new NotImplementedException();
        public DateTimeOffset FireTimeUtc => DateTimeOffset.UtcNow;
        public DateTimeOffset? ScheduledFireTimeUtc => null;
        public DateTimeOffset? PreviousFireTimeUtc => null;
        public DateTimeOffset? NextFireTimeUtc => null;
        public string FireInstanceId => "test-fire";
        public object? Result { get; set; }
        public TimeSpan JobRunTime => TimeSpan.Zero;
        public void Put(object key, object? objectValue) { }
        public object? Get(object key) => null;
    }
}
