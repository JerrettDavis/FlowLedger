using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.BillSheet;

/// <summary>
/// Reads a CSV exported from the owner's budget workbook (one or more tabs concatenated).
/// Columns are mapped by header name. A row containing "Estimated Cost" is a header and starts a
/// section (named by its first cell); rows starting with "Total" end the section's data.
/// </summary>
public static class SheetCsvImporter
{
    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames
        .Where(n => n.Length > 0).ToArray();

    private enum Role { None, Payer, Debt, Perpetual, Optional, Scope, Service, AutoPay, Reference, Due, Estimated, Actual, Month }

    public sealed record Parsed(IReadOnlyList<SheetBillData> Bills, IReadOnlyList<SheetPaymentData> Payments, int Sections);

    public static Parsed Parse(string csv, DateOnly today, string defaultSection = "Bills")
    {
        var rows = ReadRows(csv);
        var bills = new List<SheetBillData>();
        var payments = new List<SheetPaymentData>();
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Role[]? roles = null;
        DateOnly[]? monthCols = null;
        var section = defaultSection;
        var inTotals = false;
        var order = 0;

        foreach (var row in rows)
        {
            if (row.Any(c => c.Trim().Equals("Estimated Cost", StringComparison.OrdinalIgnoreCase)))
            {
                (roles, monthCols) = MapHeader(row, today);
                var title = row.Length > 0 ? row[0].Trim() : "";
                section = title.Length > 0 ? title : (sections.Count == 0 ? defaultSection : $"{defaultSection} {sections.Count + 1}");
                inTotals = false;
                order = 0;
                continue;
            }

            if (roles is null || row.Length == 0)
            {
                continue;
            }

            var name = row[0].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (name.StartsWith("total", StringComparison.OrdinalIgnoreCase) || name.StartsWith("grand total", StringComparison.OrdinalIgnoreCase))
            {
                inTotals = true;
                continue;
            }

            if (inTotals)
            {
                continue;
            }

            string Cell(Role r) => Find(row, roles, r);
            var estimated = Money(Cell(Role.Estimated)) ?? 0m;
            var actual = Money(Cell(Role.Actual));
            var bill = new SheetBillData(
                Guid.NewGuid(), section, order++, name,
                NullIfBlank(Cell(Role.Payer)),
                Yes(Cell(Role.Debt)), Yes(Cell(Role.Perpetual)), Yes(Cell(Role.Optional)),
                Cell(Role.Scope).StartsWith("bus", StringComparison.OrdinalIgnoreCase),
                Cell(Role.AutoPay).StartsWith("auto", StringComparison.OrdinalIgnoreCase),
                DueDay(Cell(Role.Due)), estimated, actual,
                NullIfBlank(Cell(Role.Service)), NullIfBlank(Cell(Role.Reference)), null, true);
            bills.Add(bill);
            sections.Add(section);

            for (var i = 0; i < row.Length && monthCols is not null; i++)
            {
                if (roles[i] == Role.Month && row[i].Trim().Equals("x", StringComparison.OrdinalIgnoreCase))
                {
                    payments.Add(new SheetPaymentData(
                        bill.Id, monthCols[i], true, actual ?? estimated, null, null, SheetPaymentSource.Manual));
                }
            }
        }

        return new Parsed(bills, payments, sections.Count);
    }

    private static (Role[] Roles, DateOnly[] Months) MapHeader(string[] header, DateOnly today)
    {
        var roles = new Role[header.Length];
        var months = new DateOnly[header.Length];
        for (var i = 1; i < header.Length; i++)
        {
            var h = header[i].Trim().ToLowerInvariant();
            roles[i] = h switch
            {
                "payer" => Role.Payer,
                "debt" => Role.Debt,
                "perpetual" => Role.Perpetual,
                "optional" => Role.Optional,
                "business/personal" => Role.Scope,
                "service" => Role.Service,
                "auto/manual" => Role.AutoPay,
                "account number" or "description" or "desciption" => Role.Reference,
                "due date" => Role.Due,
                "estimated cost" => Role.Estimated,
                "actual cost" => Role.Actual,
                _ => Role.None,
            };

            var m = Array.FindIndex(MonthNames, n => n.Equals(header[i].Trim(), StringComparison.OrdinalIgnoreCase));
            if (m >= 0)
            {
                roles[i] = Role.Month;
                var year = m + 1 <= today.Month ? today.Year : today.Year - 1;
                months[i] = new DateOnly(year, m + 1, 1);
            }
        }

        // The workbook's second header row leaves the Payer header blank; column B still holds the payer.
        if (!roles.Contains(Role.Payer) && header.Length > 1 && header[1].Trim().Length == 0)
        {
            roles[1] = Role.Payer;
        }

        return (roles, months);
    }

    private static string Find(string[] row, Role[] roles, Role role)
    {
        for (var i = 0; i < roles.Length && i < row.Length; i++)
        {
            if (roles[i] == role)
            {
                return row[i].Trim();
            }
        }

        return string.Empty;
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool Yes(string s) => s.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1";

    private static int? DueDay(string s)
    {
        var m = Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out var d) && d is >= 1 and <= 31 ? d : null;
    }

    private static decimal? Money(string s)
    {
        var cleaned = s.Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, newlines inside quotes.</summary>
    internal static List<string[]> ReadRows(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;

        void EndField()
        {
            fields.Add(sb.ToString());
            sb.Clear();
        }

        void EndRow()
        {
            EndField();
            if (fields.Any(f => f.Length > 0))
            {
                rows.Add(fields.ToArray());
            }

            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        if (sb.Length > 0 || fields.Count > 0)
        {
            EndRow();
        }

        return rows;
    }
}
