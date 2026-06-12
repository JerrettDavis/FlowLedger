namespace FlowLedger.Application.Features.Imports;

/// <summary>
/// Minimal RFC-4180-compliant CSV parser (no external dependencies).
///
/// Handles:
/// - Quoted fields (double-quote delimiter, embedded quotes doubled: "")
/// - Embedded commas, newlines inside quoted fields
/// - Configurable delimiter (default ',')
/// - CRLF and LF line endings
/// </summary>
public static class CsvParser
{
    /// <summary>
    /// Parses CSV text into rows of string fields.
    /// Each inner list is one row's fields.
    /// </summary>
    public static List<List<string>> Parse(string csvText, char delimiter = ',')
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(csvText))
        {
            return rows;
        }

        var pos = 0;
        var len = csvText.Length;

        while (pos < len)
        {
            var row = ParseRow(csvText, ref pos, len, delimiter);
            if (row is null)
            {
                break;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<string>? ParseRow(string text, ref int pos, int len, char delimiter)
    {
        if (pos >= len)
        {
            return null;
        }

        var fields = new List<string>();

        while (true)
        {
            // End of input
            if (pos >= len)
            {
                // Return what we have (may be empty field after trailing delimiter)
                return fields;
            }

            char c = text[pos];

            if (c == '"')
            {
                // Quoted field
                fields.Add(ReadQuotedField(text, ref pos, len));
            }
            else
            {
                // Unquoted field
                fields.Add(ReadUnquotedField(text, ref pos, len, delimiter));
            }

            if (pos >= len)
            {
                return fields;
            }

            c = text[pos];

            if (c == delimiter)
            {
                pos++;
                // If we're at EOL/EOF after delimiter, add empty trailing field
                if (pos >= len || text[pos] == '\r' || text[pos] == '\n')
                {
                    fields.Add(string.Empty);
                    SkipLineEnding(text, ref pos, len);
                    return fields;
                }
                continue;
            }

            // Line ending — end of row
            SkipLineEnding(text, ref pos, len);
            return fields;
        }
    }

    private static string ReadQuotedField(string text, ref int pos, int len)
    {
        pos++; // skip opening quote
        var sb = new System.Text.StringBuilder();

        while (pos < len)
        {
            char c = text[pos];
            if (c == '"')
            {
                pos++;
                if (pos < len && text[pos] == '"')
                {
                    // Escaped quote
                    sb.Append('"');
                    pos++;
                }
                else
                {
                    // Closing quote
                    return sb.ToString();
                }
            }
            else
            {
                sb.Append(c);
                pos++;
            }
        }

        // Unterminated quoted field — return what we have
        return sb.ToString();
    }

    private static string ReadUnquotedField(string text, ref int pos, int len, char delimiter)
    {
        int start = pos;
        while (pos < len)
        {
            char c = text[pos];
            if (c == delimiter || c == '\r' || c == '\n')
            {
                break;
            }

            pos++;
        }
        return text[start..pos].Trim();
    }

    private static void SkipLineEnding(string text, ref int pos, int len)
    {
        if (pos < len && text[pos] == '\r')
        {
            pos++;
        }

        if (pos < len && text[pos] == '\n')
        {
            pos++;
        }
    }
}
