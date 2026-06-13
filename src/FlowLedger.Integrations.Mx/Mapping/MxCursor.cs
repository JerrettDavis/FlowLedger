using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Mx.Mapping;

/// <summary>
/// Opaque-cursor codec for MX pagination.
///
/// MX paginates transactions with <c>page</c> (1-based) and <c>records_per_page</c>.
/// We encode that position as base64(JSON) inside the contract's opaque
/// <see cref="SyncCursor.Value"/>. Encoding is deterministic, so the same logical
/// position always produces a byte-identical cursor string — this is what satisfies
/// the contract's cursor-stability case. Advancing the page produces a different
/// string — satisfying cursor-advancement.
///
/// <see cref="SyncCursor.Initial"/> (empty value) is interpreted as page 1.
/// </summary>
internal readonly record struct MxCursor(int Page, int RecordsPerPage)
{
    private const int DefaultRecordsPerPage = 100;

    public static MxCursor First(int recordsPerPage) =>
        new(1, NormalizeRecordsPerPage(recordsPerPage));

    /// <summary>Returns the cursor for the next page, preserving page size.</summary>
    public MxCursor Next() => this with { Page = Page + 1 };

    /// <summary>
    /// Decodes a <see cref="SyncCursor"/> into an <see cref="MxCursor"/>.
    /// An initial/empty cursor returns page 1 silently.
    /// An unparseable (corrupted) cursor also falls back to page 1 — but logs a warning
    /// via <paramref name="logger"/> so the reset is observable in diagnostics.
    /// Callers must treat a page-1 result after a non-initial cursor as a pagination restart
    /// that may produce duplicate imports for the affected sync window.
    /// </summary>
    public static MxCursor Decode(SyncCursor cursor, int fallbackRecordsPerPage, ILogger? logger = null)
    {
        var rpp = NormalizeRecordsPerPage(fallbackRecordsPerPage);

        if (cursor is null || cursor.IsInitial)
        {
            return new MxCursor(1, rpp);
        }

        try
        {
            var bytes = Convert.FromBase64String(cursor.Value);
            var dto = JsonSerializer.Deserialize(bytes, MxCursorJsonContext.Default.MxCursorState);
            if (dto is null)
            {
                // Deserialised to null — treat as corrupt and restart pagination.
                logger?.LogWarning(
                    "MX sync cursor decoded to null; pagination is restarting from page 1. " +
                    "This may cause duplicate imports. Cursor value length: {Length}.",
                    cursor.Value?.Length ?? 0);
                return new MxCursor(1, rpp);
            }

            var page = dto.Page <= 0 ? 1 : dto.Page;
            var records = dto.RecordsPerPage <= 0 ? rpp : dto.RecordsPerPage;
            return new MxCursor(page, NormalizeRecordsPerPage(records));
        }
        catch (FormatException ex)
        {
            // Cursor value is not valid base-64 — it is corrupted. Restart pagination.
            logger?.LogWarning(ex,
                "MX sync cursor could not be base64-decoded; pagination is restarting from page 1. " +
                "This may cause duplicate imports. Cursor value length: {Length}.",
                cursor.Value?.Length ?? 0);
            return new MxCursor(1, rpp);
        }
        catch (JsonException ex)
        {
            // Cursor contains valid base-64 but the inner JSON is malformed — it is corrupted.
            logger?.LogWarning(ex,
                "MX sync cursor JSON is malformed; pagination is restarting from page 1. " +
                "This may cause duplicate imports. Cursor value length: {Length}.",
                cursor.Value?.Length ?? 0);
            return new MxCursor(1, rpp);
        }
    }

    /// <summary>Encodes this position back into an opaque <see cref="SyncCursor"/>.</summary>
    public SyncCursor Encode()
    {
        var state = new MxCursorState(Page, RecordsPerPage);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, MxCursorJsonContext.Default.MxCursorState);
        return new SyncCursor(Convert.ToBase64String(bytes));
    }

    private static int NormalizeRecordsPerPage(int value) =>
        value <= 0 ? DefaultRecordsPerPage : Math.Min(value, 1000);
}

/// <summary>Serialisable cursor state. Stable JSON property order → deterministic encoding.</summary>
internal sealed record MxCursorState(
    [property: JsonPropertyName("p")] int Page,
    [property: JsonPropertyName("r")] int RecordsPerPage);

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(MxCursorState))]
internal sealed partial class MxCursorJsonContext : JsonSerializerContext;
