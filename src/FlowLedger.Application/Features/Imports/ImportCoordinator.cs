using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Duplicates;
using FlowLedger.Application.Features.Integrations;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Application.Features.Imports;

/// <summary>What was detected plus the import summary.</summary>
public sealed record AutoImportResult(string FormatId, string FormatName, IReadOnlyList<string> Warnings, ImportSummaryDto Summary);

/// <summary>
/// Owns an import end to end: run the import, then the follow-up steps that belong to every import path (announce it to the
/// integrations, fold the overlaps with what the bank already synced, refresh the bill sheet). Follow-up failures are logged and
/// never fail an import that already succeeded.
/// </summary>
public sealed class ImportCoordinator(
    ImportTransactionsHandler handler,
    ISheetAutoMarker sheetMarker,
    DuplicateService duplicates,
    IntegrationService integrations,
    ILogger<ImportCoordinator> logger)
{
    /// <summary>Imports already-mapped CSV rows. Throws <see cref="InvalidOperationException"/> when the import is refused.</summary>
    public async Task<ImportSummaryDto> ImportAsync(ImportTransactionsCommand command, CancellationToken ct = default)
    {
        var summary = await handler.HandleAsync(command, ct);
        await AfterImportAsync(summary, ct);
        return summary;
    }

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

    private async Task AfterImportAsync(ImportSummaryDto summary, CancellationToken ct)
    {
        if (summary.ImportedCount <= 0)
        {
            return;
        }

        await integrations.DispatchAsync(IntegrationEvents.ImportCompleted, new { imported = summary.ImportedCount }, ct);

        // A file often overlaps what the bank already synced: fold the unambiguous overlaps now.
        try
        {
            await duplicates.AutoMergeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Duplicate merge after import failed.");
        }

        try
        {
            await sheetMarker.RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Bill sheet refresh after import failed.");
        }
    }
}
