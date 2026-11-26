using System.Globalization;
using System.Text;

namespace FlowLedger.Infrastructure.Ai.Local;

/// <summary>
/// BERT-style uncased tokenizer (lower-case, strip accents, split on punctuation, greedy longest-match WordPiece), enough for
/// MiniLM / bge / e5-style vocab.txt files. Hand-rolled (about 100 lines) to avoid a heavy tokenizer dependency.
/// </summary>
internal sealed class WordPieceTokenizer
{
    private const int MaxCharsPerWord = 100;
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unk, _cls, _sep, _pad;

    public WordPieceTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var line in File.ReadLines(vocabPath))
        {
            _vocab.TryAdd(line.TrimEnd('\r', '\n'), i++);
        }

        _unk = _vocab.GetValueOrDefault("[UNK]", 100);
        _cls = _vocab.GetValueOrDefault("[CLS]", 101);
        _sep = _vocab.GetValueOrDefault("[SEP]", 102);
        _pad = _vocab.GetValueOrDefault("[PAD]", 0);
    }

    public int PadId => _pad;

    /// <summary>[CLS] pieces... [SEP], truncated so the total never exceeds <paramref name="maxTokens"/>.</summary>
    public int[] Encode(string text, int maxTokens)
    {
        var ids = new List<int>(maxTokens) { _cls };
        foreach (var word in BasicTokens(text))
        {
            if (ids.Count >= maxTokens - 1)
            {
                break;
            }

            foreach (var piece in WordPiece(word))
            {
                if (ids.Count >= maxTokens - 1)
                {
                    break;
                }

                ids.Add(piece);
            }
        }

        ids.Add(_sep);
        return [.. ids];
    }

    private IEnumerable<int> WordPiece(string word)
    {
        if (word.Length > MaxCharsPerWord)
        {
            yield return _unk;
            yield break;
        }

        var pieces = new List<int>();
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var found = -1;
            while (start < end)
            {
                var sub = word[start..end];
                if (start > 0)
                {
                    sub = "##" + sub;
                }

                if (_vocab.TryGetValue(sub, out var id))
                {
                    found = id;
                    break;
                }

                end--;
            }

            if (found < 0)
            {
                yield return _unk;
                yield break;
            }

            pieces.Add(found);
            start = end;
        }

        foreach (var p in pieces)
        {
            yield return p;
        }
    }

    private static IEnumerable<string> BasicTokens(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || char.IsControl(ch))
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            else if (IsPunctuation(ch, cat))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }

                yield return ch.ToString();
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static bool IsPunctuation(char ch, UnicodeCategory cat)
        => (ch is >= '!' and <= '/') || (ch is >= ':' and <= '@') || (ch is >= '[' and <= '`') || (ch is >= '{' and <= '~')
           || cat is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
               or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
               or UnicodeCategory.OtherPunctuation;
}
