using FlowLedger.Application.Abstractions;
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
    ];

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
        if (rows.Count == 0)
        {
            return new ImportSummaryDto(Guid.NewGuid(), 0, 0, 0, 0, []);
        }

        int startRow = mapping.HasHeaderRow ? 1 : 0;
        var batchId = Guid.NewGuid();

        var rowErrors = new List<RowErrorDto>();
        var uniqueTransactions = new List<(Transaction Tx, string Fp)>();
        var seenFingerprints = new HashSet<string>(); // within-file dedup
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
                var (tx, fp) = ParseRow(fields, rowNumber, tenantId, accountId, mapping, dateFormats);

                if (!seenFingerprints.Add(fp))
                {
                    // Duplicate within this file
                    inFileDupCount++;
                    continue;
                }

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

    private static (Transaction Tx, string FingerprintValue) ParseRow(
        List<string> fields,
        int rowNumber,
        TenantId tenantId,
        AccountId accountId,
        CsvColumnMapping mapping,
        string[] dateFormats)
    {
        string Get(int idx) => idx < fields.Count ? fields[idx].Trim() : string.Empty;

        // Date
        var dateRaw = Get(mapping.DateColumnIndex);
        if (!TryParseDate(dateRaw, dateFormats, out var date))
        {
            throw new FormatException($"Cannot parse date '{dateRaw}' on row {rowNumber}.");
        }

        // Amount
        var amountRaw = Get(mapping.AmountColumnIndex);
        if (!TryParseAmount(amountRaw, out var rawAmount))
        {
            throw new FormatException($"Cannot parse amount '{amountRaw}' on row {rowNumber}.");
        }

        // Description
        var description = Get(mapping.DescriptionColumnIndex);
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException($"Description is empty on row {rowNumber}.");
        }

        // Optional
        var merchant = mapping.MerchantColumnIndex.HasValue ? Get(mapping.MerchantColumnIndex.Value) : null;

        // Direction: negative amount = debit, positive = credit
        var direction = rawAmount < 0m ? TransactionDirection.Debit : TransactionDirection.Credit;
        var absAmount = new Money(Math.Abs(rawAmount), new Currency("USD"));

        // Fingerprint
        var normalizedDesc = NormalizeDescription(description);
        var fingerprint = TransactionFingerprint.Create(
            accountId,
            date,
            absAmount.Amount,
            normalizedDesc);

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
            tx.Categorize(CategoryId.New(), merchant);
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

        // Fallback: try general parse
        if (DateTime.TryParse(raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var dt))
        {
            result = DateOnly.FromDateTime(dt);
            return true;
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

    private static string NormalizeDescription(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "UNKNOWN";
        }

        return System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"\s+", " ").ToUpperInvariant();
    }
}

// ── Manual match / unmatch handlers ──────────────────────────────────────────

/// <summary>
/// Manually links an actual transaction to a planned occurrence.
/// </summary>
public sealed class MatchTransactionHandler
{
    private readonly ITransactionRepository _txRepo;
    private readonly IPlannedOccurrenceRepository _occurrenceRepo;

    public MatchTransactionHandler(
        ITransactionRepository txRepo,
        IPlannedOccurrenceRepository occurrenceRepo)
    {
        _txRepo = txRepo;
        _occurrenceRepo = occurrenceRepo;
    }

    public async Task<MatchResultDto> HandleAsync(
        MatchTransactionCommand command,
        CancellationToken ct = default)
    {
        var tx = await _txRepo.GetByIdAsync(TransactionId.From(command.TransactionId), ct)
                 ?? throw new InvalidOperationException($"Transaction {command.TransactionId} not found.");

        var flow = await _occurrenceRepo.GetOwningFlowAsync(
                       PlannedOccurrenceId.From(command.PlannedOccurrenceId), ct)
                   ?? throw new InvalidOperationException(
                       $"PlannedOccurrence {command.PlannedOccurrenceId} not found.");

        var occurrence = flow.Occurrences.FirstOrDefault(
                             o => o.PlannedOccurrenceId == PlannedOccurrenceId.From(command.PlannedOccurrenceId))
                         ?? throw new InvalidOperationException(
                             $"PlannedOccurrence {command.PlannedOccurrenceId} not found in flow.");

        // Manual match uses Certain confidence
        var confidence = ConfidenceScore.Certain;
        occurrence.MatchActual(tx.TransactionId, tx.Amount, tx.EffectiveDate, confidence);
        tx.Match(occurrence.PlannedOccurrenceId, confidence);

        await _occurrenceRepo.SaveChangesAsync(ct);
        await _txRepo.SaveChangesAsync(ct);

        return MapResult(tx, occurrence);
    }

    private static MatchResultDto MapResult(Transaction tx, PlannedFlowOccurrence occ) =>
        new(tx.Id, occ.Id,
            tx.Status.ToString(),
            occ.Status.ToString(),
            occ.MatchConfidence?.Value,
            occ.AmountVariance?.Amount,
            occ.DateVarianceDays);
}

/// <summary>
/// Removes a match link between an actual transaction and a planned occurrence.
/// </summary>
public sealed class UnmatchTransactionHandler
{
    private readonly ITransactionRepository _txRepo;
    private readonly IPlannedOccurrenceRepository _occurrenceRepo;

    public UnmatchTransactionHandler(
        ITransactionRepository txRepo,
        IPlannedOccurrenceRepository occurrenceRepo)
    {
        _txRepo = txRepo;
        _occurrenceRepo = occurrenceRepo;
    }

    public async Task<MatchResultDto> HandleAsync(
        UnmatchTransactionCommand command,
        CancellationToken ct = default)
    {
        var tx = await _txRepo.GetByIdAsync(TransactionId.From(command.TransactionId), ct)
                 ?? throw new InvalidOperationException($"Transaction {command.TransactionId} not found.");

        var previousOccId = tx.Unmatch();

        if (previousOccId.HasValue)
        {
            var flow = await _occurrenceRepo.GetOwningFlowAsync(previousOccId.Value, ct);
            if (flow is not null)
            {
                var occ = flow.Occurrences.FirstOrDefault(
                    o => o.PlannedOccurrenceId == previousOccId.Value);
                occ?.Unmatch();
            }
        }

        await _occurrenceRepo.SaveChangesAsync(ct);
        await _txRepo.SaveChangesAsync(ct);

        return new MatchResultDto(tx.Id, null, tx.Status.ToString(), null, null, null, null);
    }
}

/// <summary>
/// Returns match suggestions (transactions with NeedsReview status) for the current tenant.
/// </summary>
public sealed class ListMatchSuggestionsHandler
{
    private readonly ITransactionRepository _txRepo;
    private readonly IPlannedOccurrenceRepository _occurrenceRepo;

    public ListMatchSuggestionsHandler(
        ITransactionRepository txRepo,
        IPlannedOccurrenceRepository occurrenceRepo)
    {
        _txRepo = txRepo;
        _occurrenceRepo = occurrenceRepo;
    }

    public async Task<IReadOnlyList<MatchSuggestionDto>> HandleAsync(CancellationToken ct = default)
    {
        // Get NeedsReview transactions
        var needsReview = await _txRepo.ListAsync(
            take: 200,
            ct: ct);

        var candidates = needsReview
            .Where(t => t.Status == TransactionStatus.NeedsReview)
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var minDate = candidates.Min(t => t.EffectiveDate).AddDays(-MatchingEngine.DefaultDateToleranceDays);
        var maxDate = candidates.Max(t => t.EffectiveDate).AddDays(MatchingEngine.DefaultDateToleranceDays);

        var pendingOccurrences = await _occurrenceRepo.ListPendingAsync(minDate, maxDate, ct);

        var results = new List<MatchSuggestionDto>();

        foreach (var tx in candidates)
        {
            // Find best pending occurrence
            var best = pendingOccurrences
                .Where(o => o.AccountId == tx.AccountId && o.Direction == tx.Direction)
                .OrderBy(o => Math.Abs(o.PlannedDate.DayNumber - tx.EffectiveDate.DayNumber))
                .FirstOrDefault();

            if (best is null)
            {
                continue;
            }

            int dayDiff = Math.Abs(tx.EffectiveDate.DayNumber - best.PlannedDate.DayNumber);
            decimal amtDiff = best.PlannedAmount.Amount == 0m ? 0m :
                Math.Abs(tx.Amount.Amount - best.PlannedAmount.Amount) / best.PlannedAmount.Amount;

            decimal datePart = MatchingEngine.DefaultDateToleranceDays == 0 ? 1m :
                1m - (decimal)dayDiff / MatchingEngine.DefaultDateToleranceDays;
            decimal amtPart = MatchingEngine.DefaultAmountToleranceFraction == 0m ? 1m :
                1m - amtDiff / MatchingEngine.DefaultAmountToleranceFraction;
            decimal confidence = Math.Clamp(0.40m * datePart + 0.60m * amtPart, 0m, 1m);

            results.Add(new MatchSuggestionDto(
                tx.Id,
                best.PlannedOccurrenceId.Value,
                confidence,
                tx.Amount.Amount,
                best.PlannedAmount.Amount,
                tx.Amount.Currency.Code,
                tx.EffectiveDate,
                best.PlannedDate,
                tx.Description,
                best.RecurringFlowName));
        }

        return results.AsReadOnly();
    }
}
