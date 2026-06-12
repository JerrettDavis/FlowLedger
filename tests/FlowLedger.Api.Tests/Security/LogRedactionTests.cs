using FluentAssertions;
using FlowLedger.Api.Logging;
using FlowLedger.Api.Tests.Logging;
using Serilog;
using Serilog.Events;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Unit tests for <see cref="SensitivePropertyMaskingEnricher"/>. Tests the enricher
/// directly through a Serilog pipeline with an in-memory sink — deterministic, no HTTP.
/// </summary>
public sealed class LogRedactionTests
{
    [Fact]
    public void Sensitive_values_are_redacted_in_logs()
    {
        // Arrange: a Serilog logger with the masking enricher writing to an in-memory sink.
        var sink = new ListSink();
        var logger = new LoggerConfiguration()
            .Enrich.With<SensitivePropertyMaskingEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        // Act: log an event with a sensitive-named property and a non-sensitive one.
        logger.Information("Processing request with {ApiKey} and {TenantId}",
            "super-secret-value", Guid.NewGuid());

        // Assert: ApiKey is redacted; TenantId is left intact.
        var logEvent = sink.Events.Should().ContainSingle().Subject;

        logEvent.Properties.Should().ContainKey("ApiKey");
        logEvent.Properties["ApiKey"].Should().BeOfType<ScalarValue>()
            .Which.Value.Should().Be("[REDACTED]");

        logEvent.Properties.Should().ContainKey("TenantId");
        logEvent.Properties["TenantId"].Should().BeOfType<ScalarValue>()
            .Which.Value.Should().NotBe("[REDACTED]");
    }

    [Fact]
    public void Non_sensitive_properties_are_untouched()
    {
        // Arrange
        var sink = new ListSink();
        var logger = new LoggerConfiguration()
            .Enrich.With<SensitivePropertyMaskingEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        // Act
        logger.Information("User {UserName} performed {Action}", "alice", "login");

        // Assert
        var logEvent = sink.Events.Should().ContainSingle().Subject;
        logEvent.Properties["UserName"].Should().BeOfType<ScalarValue>()
            .Which.Value.Should().Be("alice");
        logEvent.Properties["Action"].Should().BeOfType<ScalarValue>()
            .Which.Value.Should().Be("login");
    }
}
