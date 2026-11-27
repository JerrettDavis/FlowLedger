using System.Globalization;
using System.Text;

namespace FlowLedger.Application.Features.Imports;

public static partial class ImportNormalizer
{
    // ── Shared ───────────────────────────────────────────────────────────────

    private sealed record Row(DateOnly Date, decimal Amount, string Description, string? Merchant, string? Category, string? ProviderId = null);

    private static NormalizedImport Finish(string formatId, List<Row> rows, List<string> warnings, int skipped)
    {
        if (rows.Count == 0)
        {
            throw new ImportFailedException("No transactions could be read from this file.");
        }

        if (rows.Count > ImportTransactionsHandler.MaxImportRows)
        {
            throw new ImportFailedException(
                $"The file has {rows.Count} transactions; the limit per import is {ImportTransactionsHandler.MaxImportRows}. Split the file and import it in parts.");
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

    private static bool TypeColumnIsDebitCredit(List<List<string>> dataRows, int typeCol)
    {
        var values = dataRows
            .Select(r => typeCol < r.Count ? r[typeCol].Trim() : "")
            .Where(v => v.Length > 0)
            .Select(v => v.ToLowerInvariant())
            .Distinct()
            .ToList();
        return values.Count > 0 && values.All(v => v is "debit" or "credit");
    }

    private enum DateOrder { MonthFirst, DayFirst }

    private static DateOrder ResolveDateOrder(IEnumerable<string> cells, string formatId, ImportHints hints, List<string> warnings)
    {
        if (hints.DateOrder is { Length: > 0 } hint)
        {
            return hint.ToLowerInvariant() switch
            {
                "mdy" => DateOrder.MonthFirst,
                "dmy" => DateOrder.DayFirst,
                _ => throw new ImportFailedException("dateOrder must be 'mdy' or 'dmy'."),
            };
        }

        var monthFirstFails = false;
        var dayFirstFails = false;
        var ambiguous = false;
        foreach (var raw in cells.Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            if (DateTime.TryParseExact(raw, NeutralDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                continue;
            }

            var mdy = TryExact(raw, MonthFirstDateFormats, out var m);
            var dmy = TryExact(raw, DayFirstDateFormats, out var d);
            if (!mdy && !dmy)
            {
                continue; // unreadable under either order: skipped later as an unreadable row
            }

            monthFirstFails |= !mdy;
            dayFirstFails |= !dmy;
            ambiguous |= mdy && dmy && m != d;
        }

        if (monthFirstFails && dayFirstFails)
        {
            throw new ImportFailedException(
                "The dates in this file mix month/day and day/month order. Fix the file or import it in parts.");
        }

        if (dayFirstFails)
        {
            return DateOrder.MonthFirst;
        }

        if (monthFirstFails)
        {
            warnings.Add("Dates were read as day/month/year (some dates have a day above 12).");
            return DateOrder.DayFirst;
        }

        if (ambiguous)
        {
            // Every date is valid in both orders. US institutions are month-first; for a generic file there is no safe guess.
            if (formatId == Generic)
            {
                throw new ImportFailedException(
                    "The dates in this file could be month/day or day/month (for example 03/04/2025). Choose a date format (dateOrder: 'mdy' or 'dmy') and import again.");
            }

            warnings.Add("Dates were read as month/day/year; every date in the file is valid in both orders.");
        }

        return DateOrder.MonthFirst;
    }

    private static bool TryExact(string raw, string[] formats, out DateTime dt) =>
        DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt);

    private static char ResolveDecimalMark(IEnumerable<string> cells, char delimiter, ImportHints hints, List<string> warnings)
    {
        if (hints.NumberFormat is { Length: > 0 } hint)
        {
            return hint.ToLowerInvariant() switch
            {
                "us" => '.',
                "eu" => ',',
                _ => throw new ImportFailedException("numberFormat must be 'us' or 'eu'."),
            };
        }

        var dotVotes = 0;
        var commaVotes = 0;
        var ambiguous = false;
        foreach (var raw in cells)
        {
            var token = AmountToken(raw);
            var lastDot = token.LastIndexOf('.');
            var lastComma = token.LastIndexOf(',');
            if (lastDot < 0 && lastComma < 0)
            {
                continue;
            }

            if (lastDot >= 0 && lastComma >= 0)
            {
                if (lastComma > lastDot) { commaVotes++; } else { dotVotes++; }
                continue;
            }

            var mark = lastDot >= 0 ? '.' : ',';
            var count = token.Count(c => c == mark);
            var digitsAfter = token.Length - token.LastIndexOf(mark) - 1;
            if (count > 1)
            {
                // 1.234.567 or 1,234,567: the repeated mark groups thousands, so the other one is the decimal mark.
                if (mark == '.') { commaVotes++; } else { dotVotes++; }
            }
            else if (digitsAfter == 3)
            {
                ambiguous = true; // 1,234 or 1.234
            }
            else if (mark == '.')
            {
                dotVotes++;
            }
            else
            {
                commaVotes++;
            }
        }

        if (dotVotes > 0 && commaVotes > 0)
        {
            throw new ImportFailedException(
                "The amounts in this file mix decimal comma and decimal point formats. Fix the file or choose a number format (numberFormat: 'us' or 'eu').");
        }

        if (commaVotes > 0)
        {
            warnings.Add("Amounts were read with a decimal comma (for example 1.234,56).");
            return ',';
        }

        if (dotVotes == 0 && ambiguous && delimiter == ';')
        {
            // A semicolon-delimited export is the European case, where "1,234" may be 1.234. No safe guess.
            throw new ImportFailedException(
                "The amounts in this file could use a decimal comma or a decimal point (for example 1,234). Choose a number format (numberFormat: 'us' or 'eu') and import again.");
        }

        return '.';
    }

    private static string AmountToken(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '.' or ',')
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim('.', ',');
    }

    /// <summary>Parses a date, reading ambiguous numeric dates month-first (US). Imports use the per-file order instead.</summary>
    public static bool TryParseDate(string raw, out DateOnly date) => TryParseDate(raw, DateOrder.MonthFirst, out date);

    private static bool TryParseDate(string raw, DateOrder order, out DateOnly date)
    {
        date = default;
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return false;
        }

        const DateTimeStyles styles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
        var numeric = order == DateOrder.MonthFirst ? MonthFirstDateFormats : DayFirstDateFormats;
        if (DateTime.TryParseExact(raw, NeutralDateFormats, CultureInfo.InvariantCulture, styles, out var dt)
            || DateTime.TryParseExact(raw, numeric, CultureInfo.InvariantCulture, styles, out dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }

        return false;
    }

    public static bool TryParseAmount(string raw, out decimal amount) => TryParseAmount(raw, '.', out amount);

    /// <summary>Parses an amount whose decimal separator is <paramref name="decimalMark"/> ('.' or ',').</summary>
    public static bool TryParseAmount(string raw, char decimalMark, out decimal amount)
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

        var grouping = decimalMark == ',' ? "." : ",";
        s = s.Replace("$", "").Replace("€", "").Replace("£", "").Replace(grouping, "")
            .Replace(" ", "").Replace(" ", "").Replace("'", "");
        if (decimalMark == ',')
        {
            s = s.Replace(',', '.');
        }

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
