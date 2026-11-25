using System.Globalization;
using System.Text;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>RFC 4180 CSV for transactions, neutralising spreadsheet formula injection in text cells.</summary>
public static class TransactionCsv
{
    private const string Header = "Date,Account,Description,Merchant,Direction,Amount,Currency,Status,Category,Notes,Tags";

    public static string Build(IEnumerable<TransactionDto> rows, IReadOnlyDictionary<Guid, string>? categoryNames = null)
    {
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var t in rows)
        {
            var category = t.CategoryId is { } c && categoryNames?.TryGetValue(c, out var n) == true ? n : t.CategoryId?.ToString();
            sb.Append(string.Join(',',
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
                    Text(t.Tags is null ? null : string.Join(' ', t.Tags))))
                .Append("\r\n");
        }

        return sb.ToString();
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
