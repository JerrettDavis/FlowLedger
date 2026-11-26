using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Imports;

/// <summary>
/// Handles <see cref="ImportTransactionsCommand"/>.
///
/// Pipeline:
/// 1. Parse CSV (RFC-4180) with configurable column mapping.
/// 2. Per-row validation — collect errors, never crash on bad rows.
/// 3. Compute TransactionFingerprint for each valid row.
/// 4. Deduplicate: (a) within the file itself, (b) against stored fingerprints.
/// 5. Persist new transactions (Posted/actual).
/// 6. Run MatchingEngine over newly imported transactions.
/// 7. Return ImportSummaryDto with counts and per-row errors.
/// </summary>
public sealed class ImportTransactionsHandler
{
    // Standard date formats tried in order when the mapping supplies none.
    // Both "MMM d, yyyy" and "MMM d,yyyy" are included: real-world CSV files often split
    // "Jan 15, 2024" on the comma, causing the reconstructed value to lack the space.
    private static readonly string[] DefaultDateFormats =
    [
        "yyyy-MM-dd",
        "M/d/yyyy",
        "M/d/yy",
        "MM/dd/yyyy",
        "MM/dd/yy",
        "d-MMM-yyyy",
        "d-MMM-yy",
        "yyyy/MM/dd",
        "dd/MM/yyyy",
        "MMM d, yyyy",
        "MMM d,yyyy",
    ];

    /// <summary>Upper bound on rows per import, so a single request cannot flood a tenant (and the database).</summary>
    public const int MaxImportRows = TransactionQuota.ProcessingCap;

    private readonly ITransactionRepository _txRepo;
    private readonly IAccountRepository _accountRepo;
    private readonly IPlannedOccurrenceRepository _occurrenceRepo;
    private readonly ITenantContext _tenant;
    private readonly MatchingEngine _matcher;

    public ImportTransactionsHandler(
        ITransactionRepository txRepo,
        IAccountRepository accountRepo,
        IPlannedOccurrenceRepository occurrenceRepo,
        ITenantContext tenant,
        MatchingEngine matcher)
    {
        _txRepo = txRepo;
        _accountRepo = accountRepo;
        _occurrenceRepo = occurrenceRepo;
        _tenant = tenant;
        _matcher = matcher;
    }

    public async Task<ImportSummaryDto> HandleAsync(
        ImportTransactionsCommand command,
        CancellationToken ct = default)
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        var accountId = AccountId.From(command.AccountId);

        // Verify account exists and belongs to tenant
        var account = await _accountRepo.GetByIdAsync(accountId, ct);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {command.AccountId} not found.");
        }

        var mapping = command.Mapping;
        var dateFormats = mapping.DateFormats?.Length > 0 ? mapping.DateFormats : DefaultDateFormats;

        // Parse CSV
        var rows = CsvParser.Parse(command.CsvContent, mapping.Delimiter);
        if (rows.Count > MaxImportRows)
        {
            throw new InvalidOperationException($"CSV has {rows.Count} rows; the limit per import is {MaxImportRows}. Split the file and import it in parts.");
        }

        if (rows.Count == 0)
        {
            return new ImportSummaryDto(Guid.NewGuid(), 0, 0, 0, 0, []);
        }

        int startRow = mapping.HasHeaderRow ? 1 : 0;
        var batchId = Guid.NewGuid();

        var rowErrors = new List<RowErrorDto>();
        var uniqueTransactions = new List<(Transaction Tx, string Fp)>();
        // Identical same-day rows are genuine repeats (two transit fares), so each repeat gets an occurrence ordinal.
        var occurrences = new Dictionary<string, int>();
        int inFileDupCount = 0;

        for (int i = startRow; i < rows.Count; i++)
        {
            int rowNumber = i + 1;
            var fields = rows[i];

            // Skip blank rows
            if (fields.Count == 0 || fields.All(f => string.IsNullOrWhiteSpace(f)))
            {
                continue;
            }

            try
            {
                var (tx, fp) = ParseRow(fields, rowNumber, tenantId, accountId, account.CurrentBalance.Currency, mapping, dateFormats, occurrences);

                uniqueTransactions.Add((tx, fp));
            }
            catch (Exception ex)
            {
                rowErrors.Add(new RowErrorDto(
                    rowNumber,
                    string.Join(mapping.Delimiter.ToString(), fields),
                    ex.Message));
            }
        }

        if (uniqueTransactions.Count == 0)
        {
            return new ImportSummaryDto(batchId, 0, inFileDupCount, rowErrors.Count, 0, rowErrors.AsReadOnly());
        }

        // Against-DB dedup
        var allFps = uniqueTransactions.Select(x => x.Fp).ToList();
        var existingFps = await _txRepo.GetExistingFingerprintsAsync(allFps, ct);

        var toInsert = uniqueTransactions.Where(x => !existingFps.Contains(x.Fp)).ToList();
        int dbDuplicates = uniqueTransactions.Count - toInsert.Count;
        int totalDuplicates = inFileDupCount + dbDuplicates;

        if (toInsert.Count > 0)
        {
            await FlowLedger.Application.Features.Transactions.TransactionQuota.EnsureRoomAsync(_txRepo, toInsert.Count, ct);
            await _txRepo.AddRangeAsync(toInsert.Select(x => x.Tx), ct);
            await _txRepo.SaveChangesAsync(ct);
        }

        // Run matching engine on newly imported transactions
        int matchedCount = 0;
        if (toInsert.Count > 0)
        {
            var newTxs = toInsert.Select(x => x.Tx).ToList();
            var suggestions = await _matcher.MatchAsync(newTxs, ct: ct);
            matchedCount = newTxs.Count(t => t.Status == TransactionStatus.Matched);

            // Persist match state changes (occurrence status + transaction status)
            if (matchedCount > 0 || suggestions.Count > 0)
            {
                await _occurrenceRepo.SaveChangesAsync(ct);
                await _txRepo.SaveChangesAsync(ct);
            }
        }

        return new ImportSummaryDto(
            batchId,
            toInsert.Count,
            totalDuplicates,
            rowErrors.Count,
            matchedCount,
            rowErrors.AsReadOnly());
    }

    // ── Row parsing ────────────────────────────────────────────────────────────

    /// <summary>
    /// When the CSV delimiter is a comma, values like <c>1,234.56</c> or <c>Jan 15, 2024</c>
    /// are split across multiple fields by the parser (RFC-4180 requires quoting such values,
    /// but real-world exports rarely do). This helper tries to reconstruct the intended value
    /// by joining the field at <paramref name="startIdx"/> with up to
    /// <paramref name="maxExtraFields"/> following fields using a comma.
    ///
    /// When <paramref name="preferLongest"/> is <c>true</c> (use for amounts), the longest
    /// coalesced form that parses is preferred — e.g. <c>-1,234.56</c> wins over <c>-1</c>.
    /// When <c>false</c> (use for dates), the shortest successful match is returned first,
    /// avoiding accidental date+garbage parses from .NET's permissive fallback parser.
    ///
    /// Returns the number of extra fields consumed (0 if the raw field parsed on its own,
    /// -1 if no combination succeeded).
    /// </summary>
    private static int TryCoalesceField<T>(
        List<string> fields,
        int startIdx,
        int maxExtraFields,
        TryParseDelegate<T> tryParse,
        out T result,
        out string rawUsed,
        bool preferLongest = false)
    {
        rawUsed = startIdx < fields.Count ? fields[startIdx].Trim() : string.Empty;

        // Build all candidate strings (index 0 = single field, index N = most coalesced).
        int upperBound = Math.Min(maxExtraFields, fields.Count - 1 - startIdx);
        var sb = new System.Text.StringBuilder(rawUsed);
        var candidates = new string[upperBound + 1];
        candidates[0] = rawUsed;
        for (int extra = 1; extra <= upperBound; extra++)
        {
            sb.Append(',');
            sb.Append(fields[startIdx + extra].Trim());
            candidates[extra] = sb.ToString();
        }

        // Iterate from longest→shortest (preferLongest=true) or shortest→longest (false).
        int start = preferLongest ? upperBound : 0;
        int end = preferLongest ? 0 : upperBound;
        int step = preferLongest ? -1 : 1;

        for (int extra = start; preferLongest ? extra >= end : extra <= end; extra += step)
        {
            if (tryParse(candidates[extra], out result))
            {
                rawUsed = candidates[extra];
                return extra;
            }
        }

        result = default!;
        return -1; // could not parse
    }

    private delegate bool TryParseDelegate<T>(string raw, out T result);

    private static (Transaction Tx, string FingerprintValue) ParseRow(
        List<string> fields,
        int rowNumber,
        TenantId tenantId,
        AccountId accountId,
        Currency currency,
        CsvColumnMapping mapping,
        string[] dateFormats,
        Dictionary<string, int> occurrences)
    {
        // Date
        bool TryParseDateForCoalesce(string raw, out DateOnly d) =>
            TryParseDate(raw, dateFormats, out d);

        // preferLongest=true: ensures "Jan 15, 2024" (split to ["Jan 15","2024"]) coalesces
        // to the full date string rather than stopping at the ambiguous "Jan 15" fragment.
        // Removing the DateTime.TryParse fallback from TryParseDate prevents garbage coalesces
        // like "2024-01-10,-1" from accidentally matching as a valid date.
        int dateExtra = TryCoalesceField<DateOnly>(
            fields,
            mapping.DateColumnIndex,
            maxExtraFields: 1,
            TryParseDateForCoalesce,
            out var date,
            out var dateRaw,
            preferLongest: true);

        if (dateExtra < 0)
        {
            throw new FormatException($"Cannot parse date '{dateRaw}' on row {rowNumber}.");
        }

        // Amount — start after any extra fields consumed by the date.
        // Use preferLongest=true so "-1,234.56" (split into ["-1","234.56"]) coalesces
        // to the longer form rather than stopping at the valid-but-incomplete "-1".
        int amountStartIdx = mapping.AmountColumnIndex + (dateExtra > 0 ? dateExtra : 0);
        int amountExtra = TryCoalesceField<decimal>(
            fields,
            amountStartIdx,
            maxExtraFields: 2,
            TryParseAmount,
            out var rawAmount,
            out var amountRaw,
            preferLongest: true);

        if (amountExtra < 0)
        {
            throw new FormatException($"Cannot parse amount '{amountRaw}' on row {rowNumber}.");
        }

        // Description — start after any extra fields consumed by date and amount
        int descStartIdx = mapping.DescriptionColumnIndex + (dateExtra > 0 ? dateExtra : 0) + (amountExtra > 0 ? amountExtra : 0);
        var description = descStartIdx < fields.Count ? fields[descStartIdx].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException($"Description is empty on row {rowNumber}.");
        }

        // Optional — apply the same field-offset logic for coalesced date/amount fields
        int fieldOffset = (dateExtra > 0 ? dateExtra : 0) + (amountExtra > 0 ? amountExtra : 0);
        string GetShifted(int idx)
        {
            int shifted = idx + fieldOffset;
            return shifted < fields.Count ? fields[shifted].Trim() : string.Empty;
        }

        var merchant = mapping.MerchantColumnIndex.HasValue ? GetShifted(mapping.MerchantColumnIndex.Value) : null;

        // Direction: negative amount = debit, positive = credit
        var direction = rawAmount < 0m ? TransactionDirection.Debit : TransactionDirection.Credit;
        var absAmount = new Money(Math.Abs(rawAmount), currency);

        // Fingerprint: the Nth identical row in the file carries ordinal N-1, so a re-import matches
        // each stored row one-to-one and only a genuine surplus is inserted.
        var baseKey = TransactionFingerprint.FromRawDescription(accountId, date, absAmount.Amount, description).Value;
        occurrences.TryGetValue(baseKey, out var occurrence);
        occurrences[baseKey] = occurrence + 1;
        var fingerprint = TransactionFingerprint.FromRawDescription(
            accountId,
            date,
            absAmount.Amount,
            description,
            occurrence: occurrence);

        var tx = Transaction.RecordActual(
            tenantId,
            accountId,
            absAmount,
            direction,
            description,
            effectiveDate: date,
            postedDate: date,
            source: TransactionSource.CsvImport,
            fingerprint: fingerprint);

        if (!string.IsNullOrWhiteSpace(merchant))
        {
            tx.SetMerchantName(merchant);
        }

        return (tx, fingerprint.Value);
    }

    private static bool TryParseDate(string raw, string[] formats, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        foreach (var fmt in formats)
        {
            if (DateOnly.TryParseExact(raw, fmt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out result))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses amounts handling:
    /// - Leading minus: -100.50
    /// - Parentheses for negative: (100.50) or (100)
    /// - Currency symbols: $100.50
    /// - Comma as thousands separator: 1,234.56
    /// </summary>
    private static bool TryParseAmount(string raw, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = raw.Trim();

        // Parenthesized negative: (100.50) → -100.50
        bool isNegative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            isNegative = true;
            s = s[1..^1];
        }
        else if (s.StartsWith('-'))
        {
            isNegative = true;
            s = s[1..];
        }

        // Strip currency symbols and whitespace
        s = s.TrimStart('$', '£', '€', '¥', '₹', ' ');

        // Remove thousands separators (commas when decimal uses '.')
        s = s.Replace(",", "");

        if (!decimal.TryParse(s,
            System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture,
            out result))
        {
            return false;
        }

        if (isNegative)
        {
            result = -result;
        }

        return true;
    }
}
