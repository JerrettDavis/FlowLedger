namespace FlowLedger.Application.Features.Imports;

public sealed record ImportFormatInfo(string Id, string Name);

/// <summary>Optional user choices for things a file cannot settle by itself.</summary>
/// <param name="DateOrder">"mdy" or "dmy"; required for generic files whose dates are all ambiguous (03/04/2025).</param>
/// <param name="NumberFormat">"us" (1,234.56) or "eu" (1.234,56); required when separators cannot be told apart.</param>
public sealed record ImportHints(string? DateOrder = null, string? NumberFormat = null);

/// <summary>A file converted to FlowLedger's canonical CSV: <c>Date,Amount,Description,Merchant,Category,ProviderId</c> (ISO dates, negative = money out).</summary>
public sealed record NormalizedImport(string FormatId, string FormatName, string Csv, int RowCount, IReadOnlyList<string> Warnings)
{
    /// <summary>Column mapping that matches <see cref="Csv"/>.</summary>
    public static CsvColumnMapping Mapping { get; } = new(
        DateColumnIndex: 0, AmountColumnIndex: 1, DescriptionColumnIndex: 2,
        MerchantColumnIndex: 3, CategoryColumnIndex: 4, ProviderIdColumnIndex: 5, Delimiter: ',',
        DateFormats: ["yyyy-MM-dd"], HasHeaderRow: true);
}

/// <summary>
/// Recognises exports from other finance apps and banks (CSV, OFX/QFX, QIF) and converts them to one canonical CSV,
/// so the existing import pipeline (fingerprint de-duplication, plan matching) works for every source.
/// </summary>
public static partial class ImportNormalizer
{
    public const string Generic = "generic-csv";

    /// <summary>Upper bound on raw input, checked before anything is parsed or materialised.</summary>
    public const int MaxContentChars = 5 * 1024 * 1024;

    public static IReadOnlyList<ImportFormatInfo> Formats { get; } =
    [
        new("mint", "Mint"), new("monarch", "Monarch Money"), new("ynab", "YNAB"), new("simplifi", "Quicken Simplifi / Quicken"),
        new("chase", "Chase"), new("bofa", "Bank of America"), new("wells-fargo", "Wells Fargo"), new("citi", "Citi"),
        new("capital-one", "Capital One"), new("amex", "American Express"), new("discover", "Discover"),
        new("apple-card", "Apple Card"), new("paypal", "PayPal"), new(Generic, "Other CSV (detected from headers)"),
        new("ofx", "OFX / QFX (bank download)"), new("qif", "QIF (Quicken Interchange)"),
    ];

    // Formats whose reading does not depend on day/month order.
    private static readonly string[] NeutralDateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "d MMM yyyy", "MMM d, yyyy", "MMM d yyyy", "MMMM d, yyyy", "yyyyMMdd",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ",
    ];

    private static readonly string[] MonthFirstDateFormats =
        ["M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "M-d-yyyy", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm"];

    private static readonly string[] DayFirstDateFormats =
        ["d/M/yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "d.M.yyyy", "d-M-yyyy", "d/M/yy", "d/M/yyyy H:mm"];

    private static readonly string[] DateHeaders =
        ["transaction date", "trans. date", "trans date", "date", "posted date", "posting date", "post date", "settled date", "clearing date"];

    private static readonly string[] DescriptionHeaders =
        ["original statement", "original description", "description", "payee", "name", "details", "memo", "merchant"];

    private static readonly string[] MerchantHeaders = ["merchant", "payee", "name", "description"];

    private static readonly string[] AmountHeaders = ["amount", "amount (usd)", "net", "transaction amount", "amount (net)"];

    private static readonly string[] DebitHeaders = ["debit", "debits", "withdrawal", "withdrawals", "outflow", "money out", "paid out", "amount debit"];

    private static readonly string[] CreditHeaders = ["credit", "credits", "deposit", "deposits", "inflow", "money in", "paid in", "amount credit"];

    public static NormalizedImport Normalize(string content, string? formatId = null, ImportHints? hints = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ImportFailedException("The file is empty.");
        }

        if (content.Length > MaxContentChars)
        {
            throw new ImportFailedException("The file is too large to import in one go (limit 5 MB). Split it and import it in parts.");
        }

        content = content.TrimStart('﻿');
        var trimmed = content.TrimStart();

        if (formatId == "ofx" || (formatId is null && LooksLikeOfx(trimmed)))
        {
            return FromOfx(content);
        }

        if (formatId == "qif" || (formatId is null && trimmed.StartsWith('!')))
        {
            return FromQif(content);
        }

        return FromCsv(content, formatId, hints ?? new ImportHints());
    }

    // ── CSV ──────────────────────────────────────────────────────────────────

    private static NormalizedImport FromCsv(string content, string? requested, ImportHints hints)
    {
        var delimiter = DetectDelimiter(content);
        var rows = CsvParser.Parse(content, delimiter).Where(r => r.Any(c => c.Length > 0)).ToList();
        if (rows.Count == 0)
        {
            throw new ImportFailedException("No rows were found in the file.");
        }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var warnings = new List<string>();
        var out_ = new List<Row>();

        // Wells Fargo exports have no header: date, amount, *, blank, description.
        if (!header.Any(h => DateHeaders.Contains(h)) && IsHeaderlessWellsFargo(rows[0]))
        {
            foreach (var r in rows)
            {
                if (r.Count >= 5 && TryParseDate(r[0], out var d) && TryParseAmount(r[1], out var a))
                {
                    out_.Add(new Row(d, a, r[4], null, null));
                }
            }

            return Finish("wells-fargo", out_, warnings, 0);
        }

        int Find(string[] names, int? exclude = null)
        {
            foreach (var n in names)
            {
                var i = header.IndexOf(n);
                if (i >= 0 && i != exclude)
                {
                    return i;
                }
            }

            return -1;
        }

        var dateCol = Find(DateHeaders);
        var descCol = Find(DescriptionHeaders);
        if (dateCol < 0 || descCol < 0)
        {
            throw new ImportFailedException(
                "Could not recognise this file. Expected a header row with a date and description column (for example Date, Description, Amount).");
        }

        var merchantCol = Find(MerchantHeaders, descCol);
        var categoryCol = Find(["category"]);
        var amountCol = Find(AmountHeaders);
        var debitCol = Find(DebitHeaders);
        var creditCol = Find(CreditHeaders);
        var typeCol = Find(["transaction type"]);

        if (amountCol < 0 && debitCol < 0 && creditCol < 0)
        {
            throw new ImportFailedException("No amount column found (expected Amount, or Debit/Credit, or Outflow/Inflow).");
        }

        var formatId = requested is not null && requested != Generic && requested != "ofx" && requested != "qif"
            ? requested
            : DetectCsvFormat(header);
        // Card issuers that list purchases as positive numbers.
        var invert = formatId is "amex" or "discover" or "apple-card";

        var dataRows = rows.Skip(1).ToList();
        static string CellOf(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";

        var dateOrder = ResolveDateOrder(dataRows.Select(r => CellOf(r, dateCol)), formatId, hints, warnings);
        var decimalMark = ResolveDecimalMark(
            dataRows.SelectMany(r => new[] { CellOf(r, amountCol), CellOf(r, debitCol), CellOf(r, creditCol) }),
            delimiter, hints, warnings);

        // The "Transaction Type" column only overrides the sign for Mint-style files (unsigned amounts plus
        // debit/credit). In any other file it is a free-text label (POS, ACH, Withdrawal ...); trust the sign.
        var useTypeColumn = typeCol >= 0 && (formatId == "mint" || TypeColumnIsDebitCredit(dataRows, typeCol));

        var skipped = 0;
        foreach (var r in dataRows)
        {
            string Cell(int i) => CellOf(r, i);

            if (!TryParseDate(Cell(dateCol), dateOrder, out var date))
            {
                skipped++;
                continue;
            }

            decimal amount;
            if (amountCol >= 0 && TryParseAmount(Cell(amountCol), decimalMark, out var a))
            {
                amount = a;
                if (useTypeColumn)
                {
                    // Mint: unsigned amount plus "debit"/"credit".
                    amount = Math.Abs(amount) * (Cell(typeCol).Equals("debit", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                }
                else if (invert)
                {
                    amount = -amount;
                }
            }
            else
            {
                var hasDebit = TryParseAmount(Cell(debitCol), decimalMark, out var debit);
                var hasCredit = TryParseAmount(Cell(creditCol), decimalMark, out var credit);
                if (!hasDebit && !hasCredit)
                {
                    skipped++;
                    continue;
                }

                amount = (hasCredit ? Math.Abs(credit) : 0m) - (hasDebit ? Math.Abs(debit) : 0m);
            }

            var description = Cell(descCol);
            if (description.Length == 0)
            {
                description = Cell(merchantCol);
            }

            if (description.Length == 0)
            {
                skipped++;
                continue;
            }

            var merchant = Cell(merchantCol);
            out_.Add(new Row(date, amount, description, merchant.Length > 0 && merchant != description ? merchant : null,
                Cell(categoryCol) is { Length: > 0 } c ? c : null));
        }

        return Finish(formatId, out_, warnings, skipped);
    }

    private static string DetectCsvFormat(List<string> h)
    {
        bool Has(params string[] names) => names.All(h.Contains);

        if (Has("original description", "transaction type"))
        {
            return "mint";
        }

        if (Has("original statement") || Has("merchant", "account", "tags"))
        {
            return "monarch";
        }

        if (Has("payee", "outflow", "inflow"))
        {
            return "ynab";
        }

        if (Has("clearing date") || Has("purchaser"))
        {
            return "apple-card";
        }

        if (Has("trans. date", "post date"))
        {
            return "discover";
        }

        if (Has("card member") || Has("receipt", "description", "amount"))
        {
            return "amex";
        }

        if (Has("card no.") || Has("transaction date", "posted date", "debit", "credit"))
        {
            return "capital-one";
        }

        if (Has("status", "debit", "credit"))
        {
            return "citi";
        }

        if (Has("details", "posting date") || Has("transaction date", "post date", "type"))
        {
            return "chase";
        }

        if (Has("reference number", "payee", "address") || Has("running bal."))
        {
            return "bofa";
        }

        if (Has("gross") || Has("timezone"))
        {
            return "paypal";
        }

        if (Has("payee", "category", "tags"))
        {
            return "simplifi";
        }

        return Generic;
    }

    private static bool IsHeaderlessWellsFargo(List<string> first) =>
        first.Count >= 5 && TryParseDate(first[0], out _) && TryParseAmount(first[1], out _);

    private static char DetectDelimiter(string content)
    {
        var line = content.Split('\n', 2)[0];
        var best = ',';
        var bestCount = 0;
        foreach (var c in new[] { ',', ';', '\t', '|' })
        {
            var n = line.Count(x => x == c);
            if (n > bestCount)
            {
                best = c;
                bestCount = n;
            }
        }

        return best;
    }
}
