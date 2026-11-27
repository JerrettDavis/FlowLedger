using System.Globalization;
using System.Text;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>RFC 4180 CSV for transactions, neutralising spreadsheet formula injection in text cells.</summary>
public static class TransactionCsv
{
    private const string Header = "Date,Account,Description,Merchant,Direction,Amount,Currency,Status,Category,Notes,Tags";

    /// <summary>The header line, with its line ending (so a streamed export can write it before the first row).</summary>
    public const string HeaderLine = Header + "\r\n";

    public static string Build(IEnumerable<TransactionDto> rows, IReadOnlyDictionary<Guid, string>? categoryNames = null)
    {
        var sb = new StringBuilder(HeaderLine);
        foreach (var t in rows)
        {
            sb.Append(Row(t, categoryNames));
        }

        return sb.ToString();
    }

    /// <summary>One row, with its line ending.</summary>
    public static string Row(TransactionDto t, IReadOnlyDictionary<Guid, string>? categoryNames = null)
    {
        var category = t.CategoryId is { } c && categoryNames?.TryGetValue(c, out var n) == true ? n : t.CategoryId?.ToString();
        return string.Join(',',
            t.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            t.AccountId.ToString(),
            Text(t.Description),
            Text(t.MerchantName),
            Text(t.Direction),
            t.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            Text(t.Currency),
            Text(t.Status),
            Text(category),
            Text(t.Notes),
            Text(t.Tags is null ? null : string.Join(' ', t.Tags))) + "\r\n";
    }

    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
