using System.Globalization;
using FlowLedger.Application.Features.Users;
using FlowLedger.SharedKernel;
using FlowLedger.Worker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Worker.Tests;

public sealed class TenantFanOutTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private sealed class FakeDirectory(params Guid[] tenants) : IUserDirectory
    {
        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(tenants);
        public Task<ResolvedUser> ResolveAsync(ExternalIdentity identity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LinkedIdentity>> ListIdentitiesAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LinkCode> CreateLinkCodeAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LinkOutcome> RedeemLinkCodeAsync(Guid userId, string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UnlinkAsync(Guid tenantId, Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    private static ServiceProvider Provider(IUserDirectory? dir, Guid initialTenant = default)
    {
        var services = new ServiceCollection();
        if (dir is not null)
        {
            services.AddSingleton(dir);
        }

        services.AddScoped(_ => new ConfiguredTenantContext(initialTenant));
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ConfiguredTenantContext>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Runs_the_work_once_per_tenant_in_directory_order()
    {
        await using var sp = Provider(new FakeDirectory(A, B, C));
        var seen = new List<Guid>();

        await TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (_, t) => { seen.Add(t); return Task.CompletedTask; }, CancellationToken.None);

        seen.Should().Equal(A, B, C);
    }

    private static ServiceProvider ParallelProvider(int maxParallel, params Guid[] tenants)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:MaxParallelTenants"] = maxParallel.ToString(CultureInfo.InvariantCulture) }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IUserDirectory>(new FakeDirectory(tenants));
        services.AddSingleton<IConfiguration>(cfg);
        services.AddScoped(_ => new ConfiguredTenantContext(Guid.Empty));
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ConfiguredTenantContext>());
        return services.BuildServiceProvider();
    }

    // The WaitAsync timeouts below are only a hang guard so a regression fails instead of blocking CI; they never gate a pass.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Parallel_mode_runs_every_tenant_in_its_own_scope_and_isolates_failures()
    {
        await using var sp = ParallelProvider(3, A, B, C);
        var seen = new System.Collections.Concurrent.ConcurrentBag<(Guid Asked, Guid Active)>();
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, async (scope, t) =>
        {
            // Rendezvous: no tenant may finish until all three are in flight, which only parallel execution can satisfy.
            if (Interlocked.Increment(ref arrived) == 3)
            {
                allArrived.SetResult();
            }

            await allArrived.Task.WaitAsync(HangGuard);
            seen.Add((t, scope.GetRequiredService<ConfiguredTenantContext>().TenantId));
            if (t == B)
            {
                throw new InvalidOperationException("boom");
            }
        }, CancellationToken.None);

        seen.Should().HaveCount(3);
        seen.Should().OnlyContain(x => x.Asked == x.Active, "each tenant gets its own scope with that tenant active");
    }

    [Fact]
    public async Task MaxParallelTenants_bounds_concurrency_without_timing()
    {
        const int limit = 2;
        var tenants = Enumerable.Range(1, 6).Select(i => new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1])).ToArray();
        await using var sp = ParallelProvider(limit, tenants);
        var inFlight = 0;
        var maxInFlight = 0;
        var started = 0;
        var firstWaveFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, async (_, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            if (Interlocked.Increment(ref started) == limit)
            {
                firstWaveFull.SetResult();
            }

            await release.Task.WaitAsync(HangGuard);
            Interlocked.Decrement(ref inFlight);
        }, CancellationToken.None);

        // The bound is reached (not just respected) ...
        await firstWaveFull.Task.WaitAsync(HangGuard);
        release.SetResult();
        await run.WaitAsync(HangGuard);

        // ... every tenant ran, and concurrency never exceeded it.
        started.Should().Be(tenants.Length);
        maxInFlight.Should().Be(limit);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task A_failing_tenant_does_not_block_the_others_and_is_logged()
    {
        await using var sp = Provider(new FakeDirectory(A, B, C));
        var log = new CapturingLogger();
        var completed = new List<Guid>();

        var act = () => TenantFanOut.ForEachTenantAsync(sp, log, (_, t) =>
        {
            if (t == B)
            {
                throw new InvalidOperationException("boom");
            }

            completed.Add(t);
            return Task.CompletedTask;
        }, CancellationToken.None);

        await act.Should().NotThrowAsync();
        completed.Should().Equal(A, C);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Error is InvalidOperationException && e.Message.Contains(B.ToString()));
    }

    [Fact]
    public async Task A_failing_async_tenant_does_not_block_the_others()
    {
        await using var sp = Provider(new FakeDirectory(A, B));
        var completed = new List<Guid>();

        await TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, async (_, t) =>
        {
            await Task.Yield();
            if (t == A)
            {
                throw new InvalidOperationException("async boom");
            }

            completed.Add(t);
        }, CancellationToken.None);

        completed.Should().Equal(B);
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_scope_with_that_tenant_active()
    {
        await using var sp = Provider(new FakeDirectory(A, B), initialTenant: Guid.Empty);
        var observed = new List<(Guid Requested, Guid Context, Guid ViaInterface, ConfiguredTenantContext Instance)>();

        await TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (scope, t) =>
        {
            var ctx = scope.GetRequiredService<ConfiguredTenantContext>();
            observed.Add((t, ctx.TenantId, scope.GetRequiredService<ITenantContext>().TenantId, ctx));
            return Task.CompletedTask;
        }, CancellationToken.None);

        observed.Should().HaveCount(2);
        observed.Should().OnlyContain(o => o.Context == o.Requested && o.ViaInterface == o.Requested);
        observed[0].Instance.Should().NotBeSameAs(observed[1].Instance, "scoped context must never be shared across tenants");

        // The root-level scope is untouched by the fan-out.
        await using var after = sp.CreateAsyncScope();
        after.ServiceProvider.GetRequiredService<ConfiguredTenantContext>().TenantId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task Scoped_services_are_disposed_after_each_tenant()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUserDirectory>(new FakeDirectory(A, B));
        services.AddScoped(_ => new ConfiguredTenantContext(Guid.Empty));
        var disposed = 0;
        services.AddScoped(_ => new DisposeProbe(() => disposed++));
        await using var sp = services.BuildServiceProvider();
        var disposedAtNextTenantStart = new List<int>();

        await TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (scope, _) =>
        {
            disposedAtNextTenantStart.Add(disposed);
            scope.GetRequiredService<DisposeProbe>();
            return Task.CompletedTask;
        }, CancellationToken.None);

        disposedAtNextTenantStart.Should().Equal(0, 1);
        disposed.Should().Be(2);
    }

    [Fact]
    public async Task OperationCanceledException_from_the_work_is_rethrown_and_stops_the_run()
    {
        await using var sp = Provider(new FakeDirectory(A, B, C));
        var seen = new List<Guid>();

        var act = () => TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (_, t) =>
        {
            seen.Add(t);
            return t == B ? throw new OperationCanceledException() : Task.CompletedTask;
        }, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().Equal(A, B);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_before_any_tenant_runs()
    {
        await using var sp = Provider(new FakeDirectory(A, B));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ran = false;

        var act = () => TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (_, _) => { ran = true; return Task.CompletedTask; }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_mid_run_stops_before_the_next_tenant()
    {
        await using var sp = Provider(new FakeDirectory(A, B, C));
        using var cts = new CancellationTokenSource();
        var seen = new List<Guid>();

        var act = () => TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (_, t) =>
        {
            seen.Add(t);
            if (t == A)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().Equal(A);
    }

    [Fact]
    public async Task Without_a_user_directory_it_runs_once_under_the_scopes_configured_tenant()
    {
        await using var sp = Provider(dir: null, initialTenant: A);
        var seen = new List<(Guid Requested, Guid Context)>();

        await TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (scope, t) =>
        {
            seen.Add((t, scope.GetRequiredService<ConfiguredTenantContext>().TenantId));
            return Task.CompletedTask;
        }, CancellationToken.None);

        seen.Should().Equal([(A, A)]);
    }

    [Fact]
    public async Task An_empty_directory_runs_nothing()
    {
        await using var sp = Provider(new FakeDirectory());
        var ran = false;

        await TenantFanOut.ForEachTenantAsync(sp, NullLogger.Instance, (_, _) => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        ran.Should().BeFalse();
    }

    private sealed class DisposeProbe(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
