using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Imports;

public sealed record ImportFormatInfo(string Id, string Name);

/// <summary>A file converted to FlowLedger's canonical CSV: <c>Date,Amount,Description,Merchant,Category</c> (ISO dates, negative = money out).</summary>
public sealed record NormalizedImport(string FormatId, string FormatName, string Csv, int RowCount, IReadOnlyList<string> Warnings)
{
    /// <summary>Column mapping that matches <see cref="Csv"/>.</summary>
    public static CsvColumnMapping Mapping { get; } = new(
        DateColumnIndex: 0, AmountColumnIndex: 1, DescriptionColumnIndex: 2,
        MerchantColumnIndex: 3, CategoryColumnIndex: 4, Delimiter: ',',
        DateFormats: ["yyyy-MM-dd"], HasHeaderRow: true);
}

/// <summary>
/// Recognises exports from other finance apps and banks (CSV, OFX/QFX, QIF) and converts them to one canonical CSV,
/// so the existing import pipeline (fingerprint de-duplication, plan matching) works for every source.
/// </summary>
public static partial class ImportNormalizer
{
    public const string Generic = "generic-csv";

    public static IReadOnlyList<ImportFormatInfo> Formats { get; } =
    [
        new("mint", "Mint"), new("monarch", "Monarch Money"), new("ynab", "YNAB"), new("simplifi", "Quicken Simplifi / Quicken"),
        new("chase", "Chase"), new("bofa", "Bank of America"), new("wells-fargo", "Wells Fargo"), new("citi", "Citi"),
        new("capital-one", "Capital One"), new("amex", "American Express"), new("discover", "Discover"),
        new("apple-card", "Apple Card"), new("paypal", "PayPal"), new(Generic, "Other CSV (detected from headers)"),
        new("ofx", "OFX / QFX (bank download)"), new("qif", "QIF (Quicken Interchange)"),
    ];

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "yyyy/MM/dd", "M-d-yyyy", "d MMM yyyy", "MMM d, yyyy", "MMM d yyyy",
        "MMMM d, yyyy", "yyyyMMdd", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "dd.MM.yyyy", "d/M/yyyy", "dd/MM/yyyy",
    ];

    private static readonly string[] DateHeaders =
        ["transaction date", "trans. date", "trans date", "date", "posted date", "posting date", "post date", "settled date", "clearing date"];

    private static readonly string[] DescriptionHeaders =
        ["original statement", "original description", "description", "payee", "name", "details", "memo", "merchant"];

    private static readonly string[] MerchantHeaders = ["merchant", "payee", "name", "description"];

    private static readonly string[] AmountHeaders = ["amount", "amount (usd)", "net", "transaction amount", "amount (net)"];

    private static readonly string[] DebitHeaders = ["debit", "debits", "withdrawal", "withdrawals", "outflow", "money out", "paid out", "amount debit"];

    private static readonly string[] CreditHeaders = ["credit", "credits", "deposit", "deposits", "inflow", "money in", "paid in", "amount credit"];

    public static NormalizedImport Normalize(string content, string? formatId = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("The file is empty.");
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

        return FromCsv(content, formatId);
    }

    // ── CSV ──────────────────────────────────────────────────────────────────

    private static NormalizedImport FromCsv(string content, string? requested)
    {
        var delimiter = DetectDelimiter(content);
        var rows = CsvParser.Parse(content, delimiter).Where(r => r.Any(c => c.Length > 0)).ToList();
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("No rows were found in the file.");
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
            throw new InvalidOperationException(
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
            throw new InvalidOperationException("No amount column found (expected Amount, or Debit/Credit, or Outflow/Inflow).");
        }

        var formatId = requested is not null && requested != Generic && requested != "ofx" && requested != "qif"
            ? requested
            : DetectCsvFormat(header);
        // Card issuers that list purchases as positive numbers.
        var invert = formatId is "amex" or "discover" or "apple-card";

        var skipped = 0;
        foreach (var r in rows.Skip(1))
        {
            string Cell(int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";

            if (!TryParseDate(Cell(dateCol), out var date))
            {
                skipped++;
                continue;
            }

            decimal amount;
            if (amountCol >= 0 && TryParseAmount(Cell(amountCol), out var a))
            {
                amount = a;
                if (typeCol >= 0)
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
                var hasDebit = TryParseAmount(Cell(debitCol), out var debit);
                var hasCredit = TryParseAmount(Cell(creditCol), out var credit);
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

    // ── OFX / QFX ────────────────────────────────────────────────────────────

    private static bool LooksLikeOfx(string text) =>
        text.StartsWith("OFXHEADER", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<OFX>", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<STMTTRN>", StringComparison.OrdinalIgnoreCase);

    private static NormalizedImport FromOfx(string content)
    {
        var rows = new List<Row>();
        var skipped = 0;
        foreach (var block in StmtTrnRegex().Split(content).Skip(1))
        {
            var end = block.IndexOf("</STMTTRN>", StringComparison.OrdinalIgnoreCase);
            var body = end >= 0 ? block[..end] : block;

            string? Tag(string name)
            {
                var m = Regex.Match(body, $@"<{name}>\s*([^<\r\n]*)", RegexOptions.IgnoreCase);
                return m.Success ? Unescape(m.Groups[1].Value.Trim()) : null;
            }

            var posted = Tag("DTPOSTED") ?? Tag("DTUSER");
            if (posted is null || posted.Length < 8
                || !DateOnly.TryParseExact(posted[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !TryParseAmount(Tag("TRNAMT") ?? "", out var amount))
            {
                skipped++;
                continue;
            }

            var name = Tag("NAME");
            var memo = Tag("MEMO");
            var description = !string.IsNullOrWhiteSpace(name) ? name : memo;
            if (string.IsNullOrWhiteSpace(description))
            {
                skipped++;
                continue;
            }

            rows.Add(new Row(date, amount, description, null, null));
        }

        return Finish("ofx", rows, [], skipped);
    }

    [GeneratedRegex(@"<STMTTRN>", RegexOptions.IgnoreCase)]
    private static partial Regex StmtTrnRegex();

    private static string Unescape(string s) =>
        s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'");

    // ── QIF ──────────────────────────────────────────────────────────────────

    private static NormalizedImport FromQif(string content)
    {
        var rows = new List<Row>();
        var skipped = 0;
        string? d = null, t = null, p = null, m = null, l = null;
        var inInvestment = false;

        void Flush()
        {
            if (d is null && t is null && p is null)
            {
                return;
            }

            var date = default(DateOnly);
            if (!inInvestment && d is not null && TryParseDate(QifDate(d), out date) && t is not null && TryParseAmount(t, out var amount)
                && (p ?? m) is { Length: > 0 } desc)
            {
                rows.Add(new Row(date, amount, desc, null, l));
            }
            else
            {
                skipped++;
            }

            d = t = p = m = l = null;
        }

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case '!':
                    Flush();
                    inInvestment = line.StartsWith("!Type:Invst", StringComparison.OrdinalIgnoreCase);
                    break;
                case 'D': d = line[1..].Trim(); break;
                case 'T' or 'U': t ??= line[1..].Trim(); break;
                case 'P': p = line[1..].Trim(); break;
                case 'M': m = line[1..].Trim(); break;
                case 'L': l = line[1..].Trim(); break;
                case '^':
                    Flush();
                    d = t = p = m = l = null;
                    break;
            }
        }

        Flush();
        return Finish("qif", rows, [], skipped);
    }

    /// <summary>QIF writes 2-digit years after an apostrophe: <c>1/ 5'24</c> means 1/5/2024.</summary>
    private static string QifDate(string s)
    {
        s = s.Replace(" ", "");
        var i = s.IndexOf('\'');
        return i >= 0 ? s[..i] + "/20" + s[(i + 1)..] : s;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private sealed record Row(DateOnly Date, decimal Amount, string Description, string? Merchant, string? Category);

    private static NormalizedImport Finish(string formatId, List<Row> rows, List<string> warnings, int skipped)
    {
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("No transactions could be read from this file.");
        }

        if (skipped > 0)
        {
            warnings.Add($"{skipped} row(s) were skipped (blank date or amount, totals, or unreadable).");
        }

        var sb = new StringBuilder("Date,Amount,Description,Merchant,Category\r\n");
        foreach (var r in rows)
        {
            sb.Append(r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Amount.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append(Quote(r.Description)).Append(',')
              .Append(Quote(r.Merchant)).Append(',')
              .Append(Quote(r.Category)).Append("\r\n");
        }

        var name = Formats.FirstOrDefault(f => f.Id == formatId)?.Name ?? formatId;
        return new NormalizedImport(formatId, name, sb.ToString(), rows.Count, warnings);
    }

    private static string Quote(string? v)
    {
        if (string.IsNullOrEmpty(v))
        {
            return "";
        }

        return v.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static bool TryParseDate(string raw, out DateOnly date)
    {
        date = default;
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return false;
        }

        if (DateTime.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }

        return false;
    }

    public static bool TryParseAmount(string raw, out decimal amount)
    {
        amount = 0m;
        var s = raw.Trim();
        if (s.Length == 0)
        {
            return false;
        }

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        if (s.EndsWith('-'))
        {
            negative = true;
            s = s[..^1];
        }

        if (s.EndsWith("CR", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2];
        }
        else if (s.EndsWith("DR", StringComparison.OrdinalIgnoreCase))
        {
            negative = !negative;
            s = s[..^2];
        }

        s = s.Replace("$", "").Replace("€", "").Replace("£", "").Replace(",", "").Replace(" ", "").Replace(" ", "");
        if (s.StartsWith('+'))
        {
            s = s[1..];
        }

        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            return false;
        }

        if (negative)
        {
            amount = -Math.Abs(amount);
        }

        return true;
    }
}
