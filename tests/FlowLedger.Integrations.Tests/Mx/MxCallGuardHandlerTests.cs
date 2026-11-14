using System.Net;
using FluentAssertions;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Mx;

public sealed class MxCallGuardHandlerTests
{
    private sealed class FakeLedger : IProviderCallLedger
    {
        public List<ProviderCallRecord> Calls { get; } = new();
        public long Count { get; set; }
        public bool FailWrites { get; set; }

        public Task RecordAsync(ProviderCallRecord call, CancellationToken ct = default)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("db down");
            }

            Calls.Add(call);
            return Task.CompletedTask;
        }

        public Task<long> CountSinceAsync(string provider, DateTimeOffset sinceUtc, CancellationToken ct = default) => Task.FromResult(Count);
    }

    private sealed class StubInner : HttpMessageHandler
    {
        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class Monitor(MxProviderOptions o) : IOptionsMonitor<MxProviderOptions>
    {
        public MxProviderOptions CurrentValue => o;

        public MxProviderOptions Get(string? name) => o;

        public IDisposable? OnChange(Action<MxProviderOptions, string?> listener) => null;
    }

    private static (HttpClient Client, StubInner Inner, FakeLedger Ledger) Build(MxProviderOptions opts, FakeLedger? ledger = null)
    {
        ledger ??= new FakeLedger();
        var inner = new StubInner();
        var handler = new MxCallGuardHandler(ledger, new Monitor(opts), NullLogger<MxCallGuardHandler>.Instance) { InnerHandler = inner };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://mx.example") }, inner, ledger);
    }

    [Fact]
    public async Task Records_each_call_with_path_only()
    {
        var (client, _, ledger) = Build(new MxProviderOptions());

        await client.GetAsync("/users/U-1/transactions?from_date=2026-01-01");

        var call = ledger.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be("GET");
        call.Path.Should().Be("/users/U-1/transactions");
        call.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Paused_sends_nothing()
    {
        var (client, inner, ledger) = Build(new MxProviderOptions { CallsPaused = true });

        var act = () => client.GetAsync("/users");

        await act.Should().ThrowAsync<ProviderCallBudgetExceededException>();
        inner.Sent.Should().Be(0);
        ledger.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Daily_budget_exhausted_sends_nothing()
    {
        var (client, inner, _) = Build(new MxProviderOptions { DailyCallBudget = 10 }, new FakeLedger { Count = 10 });

        var act = () => client.GetAsync("/users");

        await act.Should().ThrowAsync<ProviderCallBudgetExceededException>().WithMessage("*Daily*");
        inner.Sent.Should().Be(0);
    }

    [Fact]
    public async Task Monthly_budget_exhausted_sends_nothing()
    {
        var (client, inner, _) = Build(new MxProviderOptions { DailyCallBudget = 0, MonthlyCallBudget = 5 }, new FakeLedger { Count = 5 });

        var act = () => client.GetAsync("/users");

        await act.Should().ThrowAsync<ProviderCallBudgetExceededException>().WithMessage("*Monthly*");
        inner.Sent.Should().Be(0);
    }

    [Fact]
    public async Task Under_budget_sends()
    {
        var (client, inner, _) = Build(new MxProviderOptions { DailyCallBudget = 10 }, new FakeLedger { Count = 9 });

        (await client.GetAsync("/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Sent.Should().Be(1);
    }

    [Fact]
    public async Task Ledger_write_failure_does_not_fail_a_paid_call()
    {
        var (client, inner, _) = Build(new MxProviderOptions(), new FakeLedger { FailWrites = true });

        (await client.GetAsync("/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Sent.Should().Be(1);
    }
}
