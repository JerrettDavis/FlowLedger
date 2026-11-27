namespace FlowLedger.Application.Features.Imports;

public static partial class ImportNormalizer
{
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
}
