
namespace FlowLedger.Application.Features.Imports;

/// <summary>What was detected plus the import summary.</summary>
public sealed record AutoImportResult(string FormatId, string FormatName, IReadOnlyList<string> Warnings, ImportSummaryDto Summary);

/// <summary>
/// Entry point for importing: runs the import, or detects the file format first and then runs it. The follow-ups (integration
/// webhook, duplicate merge, bill sheet refresh) are not done here: the import records a TransactionsImported event in its own
/// transaction and the outbox consumers run them (#92).
/// </summary>
internal sealed class ImportCoordinator(ImportTransactionsHandler handler)
{
    /// <summary>Imports already-mapped CSV rows. Throws <see cref="InvalidOperationException"/> when the import is refused.</summary>
    public Task<ImportSummaryDto> ImportAsync(ImportTransactionsCommand command, CancellationToken ct = default)
        => handler.HandleAsync(command, ct);

    /// <summary>
    /// Detects the file format (or uses <paramref name="formatId"/>), normalizes it and imports it. Throws
    /// <see cref="InvalidOperationException"/> when the content cannot be read or the import is refused.
    /// </summary>
    public async Task<AutoImportResult> ImportAutoAsync(
        Guid accountId, string content, string? formatId, ImportHints hints, CancellationToken ct = default)
    {
        var normalized = ImportNormalizer.Normalize(content, formatId, hints);
        var summary = await ImportAsync(new ImportTransactionsCommand(accountId, normalized.Csv, NormalizedImport.Mapping), ct);
        return new AutoImportResult(normalized.FormatId, normalized.FormatName, normalized.Warnings, summary);
    }
}
