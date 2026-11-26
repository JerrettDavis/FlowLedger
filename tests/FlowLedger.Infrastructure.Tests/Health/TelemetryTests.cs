using System.Diagnostics.Metrics;
using FlowLedger.Infrastructure.Observability;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Health;

public sealed class TelemetryTests
{
    [Fact]
    public void Provider_calls_and_job_runs_are_counted_with_outcome_tags()
    {
        var seen = new List<(string Name, long Value, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == FlowLedgerTelemetry.MeterName)
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            lock (seen)
            {
                seen.Add((inst.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))));
            }
        });
        listener.Start();

        FlowLedgerTelemetry.RecordProviderCall("telemetry-test", 200, failed: false);
        FlowLedgerTelemetry.RecordProviderCall("telemetry-test", 429, failed: false);
        FlowLedgerTelemetry.RecordProviderCall("telemetry-test", null, failed: true);
        FlowLedgerTelemetry.RecordJob("telemetry-job", success: false, TimeSpan.FromSeconds(3), DateTimeOffset.UtcNow);

        lock (seen)
        {
            seen.Should().Contain(("flowledger.provider.calls", 1L, "provider=telemetry-test,status=2xx"));
            seen.Should().Contain(("flowledger.provider.calls", 1L, "provider=telemetry-test,status=4xx"));
            seen.Should().Contain(("flowledger.provider.calls", 1L, "provider=telemetry-test,status=error"));
            seen.Should().Contain(("flowledger.job.runs", 1L, "job=telemetry-job,outcome=failure"));
        }
    }
}
