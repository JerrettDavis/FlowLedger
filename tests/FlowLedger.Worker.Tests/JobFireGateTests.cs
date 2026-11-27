using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Worker.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Testcontainers.PostgreSql;

namespace FlowLedger.Worker.Tests;

/// <summary>Once per fire time across replicas, and one catch-up run after downtime (#187).</summary>
public sealed class JobFireGateTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>Atomic in-memory stand-in for the shared <c>job_fire_marks</c> table (the real conditional upsert is tested in Infrastructure.Tests).</summary>
    private sealed class SharedMarks : IJobFireMarks
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, DateTimeOffset> _marks = [];
        public List<DateTimeOffset> Claims { get; } = [];

        public Task<bool> TryClaimAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_marks.TryGetValue(job, out var last) && last >= fireUtc)
                {
                    return Task.FromResult(false);
                }

                _marks[job] = fireUtc;
                Claims.Add(fireUtc);
                return Task.FromResult(true);
            }
        }

        public Task<DateTimeOffset?> LastFireAsync(string job, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<DateTimeOffset?>(_marks.TryGetValue(job, out var last) ? last : null);
            }
        }

        public Task SeedAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _marks.TryAdd(job, fireUtc);
                return Task.CompletedTask;
            }
        }
    }

    private sealed class Ctx(DateTimeOffset scheduled, string fireInstanceId, JobDataMap? data = null) : IJobExecutionContext
    {
        public CancellationToken CancellationToken => CancellationToken.None;
        public IScheduler Scheduler => throw new NotImplementedException();
        public ITrigger Trigger => throw new NotImplementedException();
        public ICalendar? Calendar => throw new NotImplementedException();
        public bool Recovering => false;
        public TriggerKey RecoveringTriggerKey => throw new NotImplementedException();
        public int RefireCount => 0;
        public int RetryAttempt => 0;
        public JobDataMap MergedJobDataMap { get; } = data ?? new JobDataMap();
        public IJobDetail JobDetail { get; } = JobBuilder.Create<NoopJob>().WithIdentity("ScheduledSyncJob", "FlowLedger").Build();
        public IJob JobInstance => throw new NotImplementedException();
        public DateTimeOffset FireTimeUtc => scheduled.AddSeconds(3); // fired a little late, as a real firing is
        public DateTimeOffset? ScheduledFireTimeUtc => scheduled;
        public DateTimeOffset? PreviousFireTimeUtc => null;
        public DateTimeOffset? NextFireTimeUtc => null;
        public string FireInstanceId => fireInstanceId;
        public object? Result { get; set; }
        public TimeSpan JobRunTime => TimeSpan.Zero;
        public static void Put(object key, object? objectValue) { }
        public static object? Get(object key) => null;
    }

    private sealed class NoopJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static ICronTrigger DailyAtNoon() => (ICronTrigger)TriggerBuilder.Create()
        .WithIdentity("ScheduledSyncJob-trigger", "FlowLedger")
        .WithCronSchedule("0 0 12 * * ?", c => c.InTimeZone(TimeZoneInfo.Utc))
        .Build();

    private JobExclusionTriggerListener Replica(IJobFireMarks marks) => new(
        new JobExclusion(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:flowledger"] = _postgres.GetConnectionString() }).Build(),
            NullLogger<JobExclusion>.Instance),
        new JobFireGate(NullLogger<JobFireGate>.Instance, marks));

    [Fact]
    public void A_late_or_misfired_firing_maps_to_the_cron_slot_it_was_due_for()
    {
        var trigger = DailyAtNoon();

        JobFireSlots.SlotAtOrBefore(trigger, Noon).Should().Be(Noon, "a firing exactly on the slot is that slot");
        JobFireSlots.SlotAtOrBefore(trigger, Noon.AddHours(7)).Should().Be(Noon, "FireAndProceed after a busy scheduler fires late for the same slot");
        JobFireSlots.SlotAtOrBefore(trigger, Noon.AddMinutes(-1)).Should().Be(Noon.AddDays(-1));
        JobFireSlots.SlotAtOrBefore(
            (ICronTrigger)TriggerBuilder.Create().WithCronSchedule("0 0 4 1 * ?", c => c.InTimeZone(TimeZoneInfo.Utc)).Build(),
            new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero)).Should().Be(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), "a monthly job is found without scanning minute by minute");
    }

    [Fact]
    public async Task Two_replicas_run_a_fire_time_once_even_when_the_second_fires_after_the_first_finished()
    {
        var marks = new SharedMarks();
        var a = Replica(marks);
        var b = Replica(marks);
        var trigger = DailyAtNoon();
        var firstA = new Ctx(Noon, "a-1");

        // Replica A claims noon and holds the advisory lock while it runs.
        (await a.VetoJobExecution(trigger, firstA)).Should().BeFalse();

        // Replica B fires the same slot while A is running: the lock vetoes it.
        (await b.VetoJobExecution(trigger, new Ctx(Noon, "b-1"))).Should().BeTrue("the job is already running elsewhere");

        // A finishes and releases the lock.
        await a.TriggerComplete(trigger, firstA, SchedulerInstruction.NoInstruction);

        // Seconds later B fires the same slot. The lock is free now; only the persisted mark can stop it.
        (await b.VetoJobExecution(trigger, new Ctx(Noon, "b-2"))).Should().BeTrue("this fire time was already run by A");
        marks.Claims.Should().Equal(Noon);

        // The next fire time is a fresh claim, and the lock was released by the vetoed attempt.
        var nextB = new Ctx(Noon.AddDays(1), "b-3");
        (await b.VetoJobExecution(trigger, nextB)).Should().BeFalse();
        await b.TriggerComplete(trigger, nextB, SchedulerInstruction.NoInstruction);
        marks.Claims.Should().Equal(Noon, Noon.AddDays(1));
    }

    [Fact]
    public async Task A_retry_attempt_does_not_need_a_new_claim_and_a_manual_trigger_is_not_a_slot()
    {
        var marks = new SharedMarks();
        var gate = new JobFireGate(NullLogger<JobFireGate>.Instance, marks);
        await marks.TryClaimAsync("ScheduledSyncJob", Noon);

        var retry = new Ctx(Noon, "r", new JobDataMap { [JobRetryScheduler.AttemptKey] = 2 });
        (await gate.MayFireAsync(DailyAtNoon(), retry)).Should().BeTrue("a retry re-runs a slot its first attempt already claimed");

        var manual = TriggerBuilder.Create().WithIdentity("manual").StartNow().Build();
        (await gate.MayFireAsync(manual, new Ctx(Noon, "m"))).Should().BeTrue("a one-off trigger is not a cron slot");
    }

    [Fact]
    public async Task Catch_up_runs_a_missed_job_once_records_a_first_start_and_leaves_an_up_to_date_job_alone()
    {
        var ran = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingJob.Reset(ran);
        var jobKey = new JobKey("ScheduledSyncJob", "FlowLedger");
        await using var provider = new ServiceCollection()
            .AddQuartz(q =>
            {
                q.UseSimpleTypeLoader();
                q.AddJob<RecordingJob>(o => o.WithIdentity(jobKey).StoreDurably());
                q.AddTrigger(o => o.ForJob(jobKey).WithIdentity("ScheduledSyncJob-trigger", "FlowLedger")
                    .WithCronSchedule("0 0 12 * * ?", c => c.InTimeZone(TimeZoneInfo.Utc)));
            })
            .BuildServiceProvider();
        var factory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await factory.GetScheduler();
        try
        {
            await scheduler.Start();
            var marks = new SharedMarks();
            var now = Noon.AddHours(2); // 14:00; today's noon fire has passed
            var clock = new FixedClock(now);
            var service = new JobCatchUpService(factory, NullLogger<JobCatchUpService>.Instance, clock, new ConfigurationBuilder().Build(), marks);

            // First start on a new database: remember the slot, run nothing.
            await service.StartAsync(CancellationToken.None);
            (await marks.LastFireAsync("ScheduledSyncJob")).Should().Be(Noon);
            ran.Task.IsCompleted.Should().BeFalse();

            // Same slot again (an immediate restart): nothing is missed.
            await service.StartAsync(CancellationToken.None);
            ran.Task.IsCompleted.Should().BeFalse();

            // Down across three noons: ONE catch-up run, stating the newest missed slot.
            var behind = new SharedMarks();
            await behind.SeedAsync("ScheduledSyncJob", Noon.AddDays(-3));
            await new JobCatchUpService(factory, NullLogger<JobCatchUpService>.Instance, clock, new ConfigurationBuilder().Build(), behind)
                .StartAsync(CancellationToken.None);

            (await ran.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(Noon.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            RecordingJob.Runs.Should().Be(1, "a missed fire time is run once, not once per missed day");
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingJob : IJob
    {
        private static TaskCompletionSource<string?> _ran = new();
        public static int Runs;

        public static void Reset(TaskCompletionSource<string?> ran)
        {
            _ran = ran;
            Runs = 0;
        }

        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            _ran.TrySetResult(context.MergedJobDataMap.TryGetValue(JobFireSlots.CatchUpKey, out var slot) ? slot?.ToString() : null);
            return ValueTask.CompletedTask;
        }
    }
}
