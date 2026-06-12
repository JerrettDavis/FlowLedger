using Serilog.Core;
using Serilog.Events;

namespace FlowLedger.Api.Tests.Logging;

/// <summary>
/// Minimal in-memory Serilog sink that captures emitted <see cref="LogEvent"/>s into a
/// list. Dependency-light alternative to Serilog.Sinks.InMemory for deterministic
/// assertions in the log-redaction unit test.
/// </summary>
public sealed class ListSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        _events.Add(logEvent);
    }
}
