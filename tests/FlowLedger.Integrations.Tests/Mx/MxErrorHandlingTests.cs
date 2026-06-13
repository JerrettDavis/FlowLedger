using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.Integrations.Mx.Mapping;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Tests that malformed JSON and corrupted cursors are handled gracefully:
/// no raw JsonException escapes the provider boundary, and cursor resets are logged.
/// </summary>
public sealed class MxErrorHandlingTests
{
    // ── ParseWebhookAsync — malformed JSON ────────────────────────────────────

    [Fact]
    public async Task ParseWebhookAsync_malformed_json_throws_FatalProviderException_not_JsonException()
    {
        var fixture = new MxWireMockFixture();
        var provider = fixture.CreateProvider();

        var badJson = Encoding.UTF8.GetBytes("this is { not valid ] JSON at all");

        Func<Task> act = () => provider.ParseWebhookAsync(badJson);

        // Must surface as FatalProviderException — NOT a raw JsonException.
        var ex = await act.Should().ThrowAsync<FatalProviderException>();
        ex.WithInnerException<System.Text.Json.JsonException>(
            "inner JsonException must be preserved for diagnostics");
    }

    [Fact]
    public async Task ParseWebhookAsync_empty_body_throws_FatalProviderException()
    {
        var fixture = new MxWireMockFixture();
        var provider = fixture.CreateProvider();

        // Empty bytes deserialises to null → FatalProviderException (the ?? path).
        var emptyBody = Array.Empty<byte>();

        Func<Task> act = () => provider.ParseWebhookAsync(emptyBody);

        await act.Should().ThrowAsync<FatalProviderException>();
    }

    [Fact]
    public async Task ParseWebhookAsync_truncated_json_throws_FatalProviderException_not_JsonException()
    {
        var fixture = new MxWireMockFixture();
        var provider = fixture.CreateProvider();

        // Truncated JSON — syntactically invalid.
        var truncated = Encoding.UTF8.GetBytes("{\"type\":\"AGGREGATION\",\"member_guid\":");

        Func<Task> act = () => provider.ParseWebhookAsync(truncated);

        await act.Should().ThrowAsync<FatalProviderException>();
    }

    // ── MxCursor.Decode — corrupted cursor logs warning ───────────────────────

    [Fact]
    public void Decode_corrupt_base64_returns_page_one_and_logs_warning()
    {
        var logger = new CapturingLogger();
        var cursor = new SyncCursor("not-base64-!@#");

        var result = MxCursor.Decode(cursor, fallbackRecordsPerPage: 25, logger: logger);

        result.Page.Should().Be(1, "corrupt cursor must restart pagination from page 1");
        result.RecordsPerPage.Should().Be(25);
        logger.HasWarning.Should().BeTrue("a warning must be emitted when a cursor cannot be decoded");
    }

    [Fact]
    public void Decode_valid_base64_but_malformed_inner_json_returns_page_one_and_logs_warning()
    {
        var logger = new CapturingLogger();
        // Valid base64 but the decoded bytes are not valid JSON for MxCursorState.
        var badJson = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not json"));
        var cursor = new SyncCursor(badJson);

        var result = MxCursor.Decode(cursor, fallbackRecordsPerPage: 50, logger: logger);

        result.Page.Should().Be(1, "corrupt JSON cursor must restart pagination from page 1");
        logger.HasWarning.Should().BeTrue("a warning must be emitted when a cursor JSON is malformed");
    }

    [Fact]
    public void Decode_initial_cursor_does_not_log()
    {
        var logger = new CapturingLogger();

        var result = MxCursor.Decode(SyncCursor.Initial, fallbackRecordsPerPage: 100, logger: logger);

        result.Page.Should().Be(1);
        logger.HasWarning.Should().BeFalse("an initial cursor is not corrupt — no warning expected");
    }

    [Fact]
    public void Decode_valid_cursor_does_not_log()
    {
        var logger = new CapturingLogger();
        var original = new MxCursor(3, 50);

        var result = MxCursor.Decode(original.Encode(), fallbackRecordsPerPage: 100, logger: logger);

        result.Page.Should().Be(3);
        result.RecordsPerPage.Should().Be(50);
        logger.HasWarning.Should().BeFalse("a well-formed cursor should not trigger any warning");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger"/> that captures whether any Warning-or-above message
    /// was emitted. Avoids a heavyweight mocking dependency.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public bool HasWarning { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                HasWarning = true;
            }
        }
    }
}
