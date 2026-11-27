using System.Globalization;
using System.Text;

namespace FlowLedger.Application.Features.Query;

// ── Lexer ──────────────────────────────────────────────────────────────────────

internal enum Tok { Ident, Number, String, Duration, Symbol, End }

internal readonly record struct Token(Tok Kind, string Text, int Pos, decimal Number = 0, string Unit = "");

internal static class QueryLexer
{
    private static readonly string[] Durations = ["mo", "d", "w", "y"];

    public static List<Token> Lex(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Line comments: // …
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            var start = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                var numText = text[start..i];
                if (!decimal.TryParse(numText, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                {
                    throw new QueryException($"Invalid number '{numText}'.", start);
                }

                // Duration suffix: 30d, 2w, 6mo, 1y (only when the letters are exactly a unit).
                var j = i;
                while (j < text.Length && char.IsLetter(text[j]))
                {
                    j++;
                }

                var suffix = text[i..j];
                if (suffix.Length > 0 && Durations.Contains(suffix) && n == Math.Floor(n))
                {
                    tokens.Add(new Token(Tok.Duration, text[start..j], start, n, suffix));
                    i = j;
                    continue;
                }

                tokens.Add(new Token(Tok.Number, numText, start, n));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(Tok.Ident, text[start..i], start));
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                var sb = new StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                    if (text[i] == quote) { closed = true; i++; break; }
                    sb.Append(text[i++]);
                }
                if (!closed)
                {
                    throw new QueryException("Unterminated string literal.", start);
                }

                tokens.Add(new Token(Tok.String, sb.ToString(), start));
                continue;
            }

            // Two-char operators first.
            if (i + 1 < text.Length)
            {
                var two = text.Substring(i, 2);
                if (two is "==" or "!=" or "<>" or "<=" or ">=" or "&&" or "||")
                {
                    tokens.Add(new Token(Tok.Symbol, two, start));
                    i += 2;
                    continue;
                }
            }

            if ("|,()=<>+-*/%".Contains(c))
            {
                tokens.Add(new Token(Tok.Symbol, c.ToString(), start));
                i++;
                continue;
            }

            throw new QueryException($"Unexpected character '{c}'.", start);
        }

        tokens.Add(new Token(Tok.End, "", text.Length));
        return tokens;
    }
}
