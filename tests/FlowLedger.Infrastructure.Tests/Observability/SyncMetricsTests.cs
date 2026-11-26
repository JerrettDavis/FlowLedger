using System.Diagnostics.Metrics;
using FlowLedger.Application.Abstractions;
using FlowLedger.Contracts;
using FlowLedger.Infrastructure.Observability;
using FlowLedger.Infrastructure.Sync;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Observability;

public sealed class SyncMetricsTests
{
    private sealed class StubSync(Func<Task<SyncResult>> run) : IFinancialSyncService
    {
        public Task<string> ConnectAsync(CancellationToken ct = default) => Task.FromResult("m");
        public Task<SyncResult> SyncAsync(CancellationToken ct = default) => run();
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<string> Outcomes { get; } = [];

        public Capture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == FlowLedgerTelemetry.MeterName && instrument.Name == "flowledger.sync.runs")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var t in tags)
                {
                    if (t.Key == "outcome")
                    {
                        lock (Outcomes)
                        {
                            Outcomes.Add((string)t.Value!);
                        }
                    }
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private static GuardedSyncService Service(Func<Task<SyncResult>> run, TimeSpan minInterval, DateTimeOffset? last = null)
        => new(new StubSync(run), Guid.NewGuid(), minInterval, _ => Task.FromResult(last));

    [Fact]
    public async Task Successful_sync_is_counted_as_success()
    {
        using var capture = new Capture();
        await Service(() => Task.FromResult(new SyncResult(1, 2, 0, 0)), TimeSpan.Zero).SyncAsync();
        capture.Outcomes.Should().Contain("success");
    }

    [Fact]
    public async Task Failing_sync_is_counted_as_failure_and_rethrown()
    {
        using var capture = new Capture();
        var act = () => Service(() => throw new InvalidOperationException("boom"), TimeSpan.Zero).SyncAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Outcomes.Should().Contain("failure");
    }

    [Fact]
    public async Task Throttled_sync_is_counted_as_throttled()
    {
        using var capture = new Capture();
        var act = () => Service(() => Task.FromResult(new SyncResult(0, 0, 0, 0)), TimeSpan.FromHours(1), DateTimeOffset.UtcNow).SyncAsync();
        await act.Should().ThrowAsync<SyncThrottledException>();
        capture.Outcomes.Should().Contain("throttled");
    }
}
