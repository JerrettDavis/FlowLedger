using System.Diagnostics.Metrics;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Observability;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

public sealed class BackupFailureNotifierTests
{
    private sealed class FakeAudit : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSender : IHomeAssistantSender
    {
        public List<(string Url, object Payload, bool AllowLocal)> Sent { get; } = [];

        public Task<string?> SendAsync(string webhookUrl, object payload, bool allowLocalNetwork, CancellationToken ct = default)
        {
            Sent.Add((webhookUrl, payload, allowLocalNetwork));
            return Task.FromResult<string?>(null);
        }
    }

    private static IConfiguration Config(params (string, string)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(k => k.Item1, k => (string?)k.Item2)).Build();

    private static BackupRunResult Result(bool ok, string? error = null) =>
        new(ok, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 0, null, null, null, error);

    private static long CountBackupMetric(Func<Task> act, string outcome)
    {
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter == FlowLedgerTelemetry.Meter && inst.Name == "flowledger.backup.runs")
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var t in tags)
            {
                if (t.Key == "outcome" && (string?)t.Value == outcome)
                {
                    Interlocked.Add(ref total, value);
                }
            }
        });
        listener.Start();
        act().GetAwaiter().GetResult();
        return Interlocked.Read(ref total);
    }

    [Fact]
    public async Task Failure_emits_a_metric_an_audit_event_and_the_webhook()
    {
        var audit = new FakeAudit();
        var sender = new FakeSender();
        var notifier = new BackupFailureNotifier(
            Config(("Backup:Alert:WebhookUrl", "https://ha.example.com/api/webhook/abc"), ("Backup:Alert:AllowLocalNetwork", "true")),
            NullLogger.Instance, audit, sender);

        var failures = CountBackupMetric(() => notifier.NotifyAsync(Result(false, "Off-site backup failed: boom")), "failure");

        failures.Should().Be(1);
        audit.Events.Should().ContainSingle().Which.Should().Match<AuditEvent>(e => e.Action == "backup.failed" && e.Target == "offsite");
        sender.Sent.Should().ContainSingle().Which.AllowLocal.Should().BeTrue();
    }

    [Fact]
    public async Task Success_records_only_the_metric()
    {
        var audit = new FakeAudit();
        var sender = new FakeSender();
        var notifier = new BackupFailureNotifier(Config(("Backup:Alert:WebhookUrl", "https://ha.example.com/x")), NullLogger.Instance, audit, sender);

        var ok = CountBackupMetric(() => notifier.NotifyAsync(Result(true)), "success");

        ok.Should().Be(1);
        audit.Events.Should().BeEmpty();
        sender.Sent.Should().BeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_a_webhook_url_nothing_is_sent()
    {
        var sender = new FakeSender();
        var notifier = new BackupFailureNotifier(Config(), NullLogger.Instance, new FakeAudit(), sender);

        await notifier.NotifyAsync(Result(false, "Restore drill failed: x"));

        sender.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Restore drill failed: x", "drill")]
    [InlineData("Off-site backup failed: x", "offsite")]
    [InlineData("connection refused", "snapshot")]
    public void Stage_is_derived_from_the_error(string error, string stage) =>
        BackupFailureNotifier.StageOf(Result(false, error)).Should().Be(stage);
}
