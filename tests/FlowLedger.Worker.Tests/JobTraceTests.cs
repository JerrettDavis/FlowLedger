using System.Diagnostics;
using FlowLedger.Application.Features.Users;
using FlowLedger.Worker;
using FlowLedger.Worker.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Worker.Tests;

/// <summary>A job run is one trace; its correlation id ties the span, the baggage and the log scope together (#193).</summary>
public sealed class JobTraceTests
{
    private sealed class ScopeCapturingLogger : ILogger
    {
        public List<IReadOnlyDictionary<string, object>> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IReadOnlyDictionary<string, object> d)
            {
                Scopes.Add(d);
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class FakeDirectory(params Guid[] tenants) : IUserDirectory
    {
        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(tenants);
        public Task<ResolvedUser> ResolveAsync(ExternalIdentity identity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LinkedIdentity>> ListIdentitiesAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LinkCode> CreateLinkCodeAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LinkOutcome> RedeemLinkCodeAsync(Guid userId, string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UnlinkAsync(Guid tenantId, Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ActivityListener Listen(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is JobTrace.SourceName or "test.child",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public async Task Everything_a_run_does_shares_one_trace_and_one_correlation_id_with_its_log_scope()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var child = new ActivitySource("test.child");
        var logger = new ScopeCapturingLogger();
        string? baggageSeen = null;

        await JobTrace.RunAsync("ScheduledSyncJob", "fire-1", 2, logger, _ =>
        {
            using var inner = child.StartActivity("db command");
            baggageSeen = Activity.Current?.GetBaggageItem(JobTrace.CorrelationKey);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        var job = stopped.Single(a => a.OperationName == "job ScheduledSyncJob");
        var db = stopped.Single(a => a.OperationName == "db command");
        job.Parent.Should().BeNull("a run is a root span");
        db.TraceId.Should().Be(job.TraceId, "work done inside the run is part of the run's trace");
        job.GetTagItem(JobTrace.CorrelationKey).Should().Be(job.TraceId.ToString());
        job.GetTagItem("job.attempt").Should().Be(2);
        baggageSeen.Should().Be(job.TraceId.ToString());
        logger.Scopes.Should().ContainSingle().Which["CorrelationId"].Should().Be(job.TraceId.ToString());
    }

    [Fact]
    public async Task A_failing_run_marks_its_span_as_an_error_and_still_throws()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);

        var act = async () => await JobTrace.RunAsync("ScheduledBackupJob", "fire-2", 1, NullLogger.Instance,
            _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        stopped.Single(a => a.OperationName == "job ScheduledBackupJob").Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Each_tenant_of_a_run_gets_a_child_span_tagged_with_its_id_in_the_same_trace()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        var a = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var b = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var services = new ServiceCollection()
            .AddSingleton<IUserDirectory>(new FakeDirectory(a, b))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        await JobTrace.RunAsync("ScheduledHomeAssistantJob", "fire-3", 1, NullLogger.Instance,
            ct => new ValueTask(TenantFanOut.ForEachTenantAsync(services, NullLogger.Instance, (_, _) => Task.CompletedTask, ct)), CancellationToken.None);

        var job = stopped.Single(s => s.OperationName == "job ScheduledHomeAssistantJob");
        var tenants = stopped.Where(s => s.OperationName == "tenant work").ToList();
        tenants.Select(t => t.GetTagItem("tenant.id")).Should().BeEquivalentTo(new object[] { a.ToString(), b.ToString() });
        tenants.Should().OnlyContain(t => t.TraceId == job.TraceId);
    }
}
