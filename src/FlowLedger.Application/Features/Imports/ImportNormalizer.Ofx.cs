using System.Globalization;
using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Imports;

public static partial class ImportNormalizer
{
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
                var m = Regex.Match(body, $@"<{name}>\s*([^<\r\n]*)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
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

            rows.Add(new Row(date, amount, description, null, null, Tag("FITID")));
        }

        return Finish("ofx", rows, [], skipped);
    }

    [GeneratedRegex(@"<STMTTRN>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex StmtTrnRegex();

    private static string Unescape(string s) =>
        s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'");
}
