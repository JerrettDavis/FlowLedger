using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using FlowLedger.TestSupport;
using FlowLedger.Worker.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;

namespace FlowLedger.Worker.Tests.Jobs;

/// <summary>
/// ADR 0018: the scheduled job defers to the shared <see cref="SyncRetryPolicy"/>. The fake connection store / sync service below
/// carry no connector identity, only <see cref="ProviderIssueKind"/>s.
/// </summary>
public sealed class ScheduledSyncJobRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Value;
    }

    private sealed class FakeStore(params ProviderConnectionInfo[] connections) : IProviderConnectionStore
    {
        public Task<IReadOnlyList<ProviderConnectionInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderConnectionInfo>>(connections);
        public Task<string?> GetMemberProviderIdAsync(ConnectorId providerName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(ConnectorId providerName, string memberProviderId, string institution, string status, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Sync(Exception? error = null) : IFinancialSyncService
    {
        public int Calls { get; private set; }
        public Task<string> ConnectAsync(CancellationToken ct = default) => Task.FromResult("m");
        public Task<SyncResult> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            return error is null ? Task.FromResult(new SyncResult(0, 0, 0, 0)) : Task.FromException<SyncResult>(error);
        }
    }

    private static ProviderConnectionInfo Connection(ProviderIssueKind? kind, int failures = 1, DateTimeOffset? at = null)
        => new("Any", "Bank", "Active", Now, Now,
            kind is null ? null : ProviderIssueMapping.ToInfo(ProviderIssue.Of(kind.Value, "m"), at ?? Now, failures));

    private static async Task<(Sync Sync, Clock Clock)> RunAsync(ProviderConnectionInfo? connection, Exception? error = null, Clock? clock = null)
    {
        clock ??= new Clock();
        var sync = new Sync(error);
        var services = new ServiceCollection();
        services.AddScoped<IFinancialSyncService>(_ => sync);
        services.AddScoped<IProviderConnectionStore>(_ => connection is null ? new FakeStore() : new FakeStore(connection));
        services.AddSingleton(new SyncRetryPolicy(clock));
        services.AddScoped<SyncAttemptGate>();
        var job = new ScheduledSyncJob(services.BuildServiceProvider(), NullLogger<ScheduledSyncJob>.Instance,
            Options.Create(new JobScheduleOptions { Enabled = true, CronExpression = "0 0 */4 * * ?" }));
        await job.Execute(new Ctx(), CancellationToken.None);
        return (sync, clock);
    }

    [Fact]
    public async Task Healthy_or_unrecorded_tenants_are_synced()
    {
        (await RunAsync(null)).Sync.Calls.Should().Be(1);
        (await RunAsync(Connection(null))).Sync.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Connections_waiting_for_the_user_are_skipped_without_calling_the_provider()
    {
        foreach (var kind in new[] { ProviderIssueKind.ReauthRequired, ProviderIssueKind.InvalidCredentials, ProviderIssueKind.Disconnected })
        {
            (await RunAsync(Connection(kind))).Sync.Calls.Should().Be(0, kind.ToString());
        }
    }

    [Fact]
    public async Task Retryable_connections_are_skipped_inside_the_back_off_window_and_retried_after_it()
    {
        var clock = new Clock();
        var connection = Connection(ProviderIssueKind.ServiceUnavailable);

        (await RunAsync(connection, clock: clock)).Sync.Calls.Should().Be(0);

        clock.Value = Now.AddHours(1);
        (await RunAsync(connection, clock: clock)).Sync.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Connections_that_exhausted_their_retries_are_not_attempted_again()
    {
        var clock = new Clock { Value = Now.AddDays(10) };
        (await RunAsync(Connection(ProviderIssueKind.Internal, failures: 10), clock: clock)).Sync.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Provider_failures_of_every_kind_are_contained_by_the_job()
    {
        foreach (var kind in Enum.GetValues<ProviderIssueKind>().Where(k => k != ProviderIssueKind.None))
        {
            var error = new TransientProviderException("boom") { Kind = kind };
            var act = () => RunAsync(null, error);
            await act.Should().NotThrowAsync(kind.ToString());
        }
    }

    private sealed class Ctx : IJobExecutionContext
    {
        public CancellationToken CancellationToken => CancellationToken.None;
        public IScheduler Scheduler => throw new NotImplementedException();
        public ITrigger Trigger => throw new NotImplementedException();
        public ICalendar? Calendar => throw new NotImplementedException();
        public bool Recovering => false;
        public TriggerKey RecoveringTriggerKey => throw new NotImplementedException();
        public int RefireCount => 0;
        public int RetryAttempt => 0;
        public JobDataMap MergedJobDataMap => new();
        public IJobDetail JobDetail { get; } = JobBuilder.Create<ScheduledSyncJob>().WithIdentity(nameof(ScheduledSyncJob), "FlowLedger").Build();
        public IJob JobInstance => throw new NotImplementedException();
        public DateTimeOffset FireTimeUtc => TestClock.Instant;
        public DateTimeOffset? ScheduledFireTimeUtc => null;
        public DateTimeOffset? PreviousFireTimeUtc => null;
        public DateTimeOffset? NextFireTimeUtc => null;
        public string FireInstanceId => "test-fire";
        public object? Result { get; set; }
        public TimeSpan JobRunTime => TimeSpan.Zero;
        public static void Put(object key, object? objectValue) { }
        public static object? Get(object key) => null;
    }
}
