using System.Diagnostics.Metrics;
using FlowLedger.Infrastructure.Scheduling;
using FlowLedger.Infrastructure.Observability;
using FlowLedger.Worker.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Testcontainers.PostgreSql;

namespace FlowLedger.Worker.Tests;

/// <summary>Once per fire time across replicas, lease-based reclaim of a dead owner's slot, fail-closed store outages, one catch-up run (#187).</summary>
public sealed class JobFireGateTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Atomic in-memory stand-in for the shared <c>job_fire_marks</c> table (the real conditional upsert is tested in Infrastructure.Tests).</summary>
    private sealed class SharedMarks(TimeProvider clock) : IJobFireMarks
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, JobFireMark> _marks = [];

        public Task<bool> TryClaimAsync(string job, DateTimeOffset fireUtc, string owner, TimeSpan lease, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var now = clock.GetUtcNow();
                if (_marks.TryGetValue(job, out var m)
                    && !(m.LastFireUtc < fireUtc || (m.LastFireUtc == fireUtc && m.CompletedUtc is null && m.ClaimedUtc <= now - lease)))
                {
                    return Task.FromResult(false);
                }

                _marks[job] = new JobFireMark(fireUtc, now, null, owner);
                return Task.FromResult(true);
            }
        }

        public Task<bool> CompleteAsync(string job, DateTimeOffset fireUtc, string owner, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_marks.TryGetValue(job, out var m) && m.LastFireUtc == fireUtc && m.Owner == owner && m.CompletedUtc is null)
                {
                    _marks[job] = m with { CompletedUtc = clock.GetUtcNow() };
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        }

        public Task<JobFireMark?> GetAsync(string job, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<JobFireMark?>(_marks.TryGetValue(job, out var m) ? m : null);
            }
        }

        public Task SeedAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _marks.TryAdd(job, new JobFireMark(fireUtc, clock.GetUtcNow(), clock.GetUtcNow(), "seed"));
                return Task.CompletedTask;
            }
        }
    }

    private sealed class DownMarks : IJobFireMarks
    {
        public int Calls { get; private set; }

        private InvalidOperationException Fail()
        {
            Calls++;
            return new InvalidOperationException("marks table unreachable");
        }

        public Task<bool> TryClaimAsync(string job, DateTimeOffset fireUtc, string owner, TimeSpan lease, CancellationToken ct = default) => throw Fail();
        public Task<bool> CompleteAsync(string job, DateTimeOffset fireUtc, string owner, CancellationToken ct = default) => throw Fail();
        public Task<JobFireMark?> GetAsync(string job, CancellationToken ct = default) => throw Fail();
        public Task SeedAsync(string job, DateTimeOffset fireUtc, CancellationToken ct = default) => throw Fail();
    }

    private sealed class Ctx(DateTimeOffset scheduled, string fireInstanceId, JobDataMap? data = null, string job = "ScheduledSyncJob") : IJobExecutionContext
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
        public IJobDetail JobDetail { get; } = JobBuilder.Create<RecordingJob>().WithIdentity(job, "FlowLedger").Build();
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

    private static ICronTrigger DailyAtNoon() => (ICronTrigger)TriggerBuilder.Create()
        .WithIdentity("ScheduledSyncJob-trigger", "FlowLedger")
        .WithCronSchedule("0 0 12 * * ?", c => c.InTimeZone(TimeZoneInfo.Utc))
        .Build();

    private static JobFireGate Gate(IJobFireMarks marks, TimeProvider clock, JobFireGateOptions? options = null)
        => new(NullLogger<JobFireGate>.Instance, marks, options ?? JobFireGateOptions.Default with { RetryDelay = TimeSpan.Zero }, clock);

    private (JobExclusion Exclusion, JobFireGate Gate, JobExclusionTriggerListener Listener) Replica(IJobFireMarks marks, TimeProvider clock)
    {
        var exclusion = new JobExclusion(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:flowledger"] = _postgres.GetConnectionString() }).Build(),
            NullLogger<JobExclusion>.Instance);
        var gate = Gate(marks, clock);
        return (exclusion, gate, new JobExclusionTriggerListener(exclusion, gate));
    }

    private static List<(string Job, string Outcome)> CaptureFireGateCounter(out MeterListener listener)
    {
        var seen = new List<(string, string)>();
        var l = new MeterListener();
        l.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Meter.Name == FlowLedgerTelemetry.MeterName && instrument.Name == "flowledger.job.firegate")
            {
                ml.EnableMeasurementEvents(instrument);
            }
        };
        l.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string job = "", outcome = "";
            foreach (var t in tags)
            {
                if (t.Key == "job")
                {
                    job = t.Value?.ToString() ?? "";
                }
                else if (t.Key == "outcome")
                {
                    outcome = t.Value?.ToString() ?? "";
                }
            }

            lock (seen)
            {
                seen.Add((job, outcome));
            }
        });
        l.Start();
        listener = l;
        return seen;
    }

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
        var clock = new MovableClock(Noon);
        var marks = new SharedMarks(clock);
        var a = Replica(marks, clock);
        var b = Replica(marks, clock);
        var trigger = DailyAtNoon();
        var firstA = new Ctx(Noon, "a-1");

        // Replica A claims noon and holds the advisory lock while it runs.
        (await a.Listener.VetoJobExecution(trigger, firstA)).Should().BeFalse();

        // Replica B fires the same slot while A is running: the lock vetoes it.
        (await b.Listener.VetoJobExecution(trigger, new Ctx(Noon, "b-1"))).Should().BeTrue("the job is already running elsewhere");

        // A finishes: the claim is confirmed and the lock released.
        await a.Listener.TriggerComplete(trigger, firstA, SchedulerInstruction.NoInstruction);
        (await marks.GetAsync("ScheduledSyncJob"))!.CompletedUtc.Should().NotBeNull();

        // Seconds later B fires the same slot. The lock is free now; only the persisted mark can stop it.
        (await b.Listener.VetoJobExecution(trigger, new Ctx(Noon, "b-2"))).Should().BeTrue("this fire time was already run by A");

        // The next fire time is a fresh claim, and the lock was released by the vetoed attempt.
        var nextB = new Ctx(Noon.AddDays(1), "b-3");
        (await b.Listener.VetoJobExecution(trigger, nextB)).Should().BeFalse();
        await b.Listener.TriggerComplete(trigger, nextB, SchedulerInstruction.NoInstruction);
        (await marks.GetAsync("ScheduledSyncJob"))!.LastFireUtc.Should().Be(Noon.AddDays(1));
    }

    [Fact]
    public async Task A_slot_whose_owner_died_is_re_fired_by_catch_up_after_the_lease_and_not_before()
    {
        var clock = new MovableClock(Noon.AddSeconds(2));
        var marks = new SharedMarks(clock);
        var trigger = DailyAtNoon();
        var dead = Replica(marks, clock);

        // Replica A claims noon, starts the run, and dies: no TriggerComplete. Its database session (and so its advisory lock) goes with it.
        (await dead.Listener.VetoJobExecution(trigger, new Ctx(Noon, "dead-1"))).Should().BeFalse();
        await dead.Exclusion.ExitAsync("dead-1");

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
            var survivor = Replica(marks, clock);
            var catchUp = new JobCatchUpService(factory, NullLogger<JobCatchUpService>.Instance, clock, new ConfigurationBuilder().Build(), survivor.Gate, marks);

            clock.Now = Noon.AddMinutes(5);
            await catchUp.CheckAsync(CancellationToken.None);
            ran.Task.IsCompleted.Should().BeFalse("the dead owner's lease (10 minutes) has not run out; it might still be running");

            clock.Now = Noon.AddMinutes(11);
            await catchUp.CheckAsync(CancellationToken.None);
            (await ran.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(Noon.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

            // The re-fired run is allowed to claim the abandoned slot, and finishing it confirms the slot for good.
            var refire = new Ctx(Noon.AddMinutes(11), "refire-1", new JobDataMap { [JobFireSlots.CatchUpKey] = Noon.ToString("O", System.Globalization.CultureInfo.InvariantCulture) });
            (await survivor.Listener.VetoJobExecution(trigger, refire)).Should().BeFalse();
            await survivor.Listener.TriggerComplete(trigger, refire, SchedulerInstruction.NoInstruction);
            clock.Now = Noon.AddHours(1);
            RecordingJob.Reset(new TaskCompletionSource<string?>());
            await catchUp.CheckAsync(CancellationToken.None);
            RecordingJob.Runs.Should().Be(0, "a confirmed slot is not re-fired");
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }

    [Fact]
    public async Task When_the_marks_table_is_down_a_job_is_skipped_by_default_and_the_decision_is_counted()
    {
        var clock = new MovableClock(Noon);
        var down = new DownMarks();
        var gate = Gate(down, clock);
        var seen = CaptureFireGateCounter(out var listener);
        using var _ = listener;

        var may = await gate.MayFireAsync(DailyAtNoon(), new Ctx(Noon, "x-1", job: "ScheduledBackupJob"));

        may.Should().BeFalse("a backup or Home Assistant push must not run twice just because the store is down; the next catch-up tick re-fires it");
        down.Calls.Should().Be(JobFireGateOptions.Default.Attempts, "the claim is retried a bounded number of times first");
        lock (seen)
        {
            seen.Should().Contain(("ScheduledBackupJob", "store_failed_skipped"));
        }
    }

    [Fact]
    public async Task The_failure_mode_can_be_switched_to_run_globally_or_per_job_and_is_counted()
    {
        var clock = new MovableClock(Noon);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:FireGate:RetryDelayMs"] = "0",
            ["Worker:FireGate:Jobs:ScheduledHomeAssistantJob:OnStoreFailure"] = "Run",
        }).Build();
        var options = JobFireGateOptions.From(config);
        var seen = CaptureFireGateCounter(out var listener);
        using var _ = listener;
        var gate = Gate(new DownMarks(), clock, options);

        (await gate.MayFireAsync(DailyAtNoon(), new Ctx(Noon, "x-2", job: "ScheduledHomeAssistantJob"))).Should().BeTrue("this job opted in to fail open");
        (await gate.MayFireAsync(DailyAtNoon(), new Ctx(Noon, "x-3", job: "ScheduledBackupJob"))).Should().BeFalse("every other job stays fail closed");

        options.OnStoreFailure.Should().Be(FireGateFailureMode.Skip);
        lock (seen)
        {
            seen.Should().Contain(("ScheduledHomeAssistantJob", "store_failed_ran"));
            seen.Should().Contain(("ScheduledBackupJob", "store_failed_skipped"));
        }
    }

    [Fact]
    public async Task A_retry_attempt_does_not_need_a_new_claim_and_a_manual_trigger_is_not_a_slot()
    {
        var clock = new MovableClock(Noon);
        var marks = new SharedMarks(clock);
        var gate = Gate(marks, clock);
        await marks.TryClaimAsync("ScheduledSyncJob", Noon, "first-attempt", TimeSpan.FromMinutes(10));

        var retry = new Ctx(Noon, "r", new JobDataMap { [JobRetryScheduler.AttemptKey] = 2 });
        (await gate.MayFireAsync(DailyAtNoon(), retry)).Should().BeTrue("a retry re-runs a slot its first attempt already claimed");

        var manual = TriggerBuilder.Create().WithIdentity("manual").StartNow().Build();
        (await gate.MayFireAsync(manual, new Ctx(Noon, "m"))).Should().BeTrue("a one-off trigger is not a cron slot");
    }

    [Fact]
    public async Task Catch_up_records_a_first_start_leaves_an_up_to_date_job_alone_and_runs_a_missed_job_once()
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
            var clock = new MovableClock(Noon.AddHours(2)); // 14:00; today's noon fire has passed
            var marks = new SharedMarks(clock);
            var config = new ConfigurationBuilder().Build();
            var service = new JobCatchUpService(factory, NullLogger<JobCatchUpService>.Instance, clock, config, Gate(marks, clock), marks);

            // First start on a new database: remember the slot, run nothing.
            await service.CheckAsync(CancellationToken.None);
            (await marks.GetAsync("ScheduledSyncJob"))!.LastFireUtc.Should().Be(Noon);
            ran.Task.IsCompleted.Should().BeFalse();

            // Same slot again (an immediate restart): nothing is missed.
            await service.CheckAsync(CancellationToken.None);
            ran.Task.IsCompleted.Should().BeFalse();

            // Down across three noons: ONE catch-up run, stating the newest missed slot.
            var behind = new SharedMarks(clock);
            await behind.SeedAsync("ScheduledSyncJob", Noon.AddDays(-3));
            await new JobCatchUpService(factory, NullLogger<JobCatchUpService>.Instance, clock, config, Gate(behind, clock), behind).CheckAsync(CancellationToken.None);

            (await ran.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(Noon.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            RecordingJob.Runs.Should().Be(1, "a missed fire time is run once, not once per missed day");
        }
        finally
        {
            await scheduler.Shutdown();
        }
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
